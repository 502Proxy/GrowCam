"""HTTP routes. Thin: parse the request, call a service, render or redirect."""

from __future__ import annotations

import contextlib
import logging
from datetime import date
from pathlib import Path
from urllib.parse import urlencode

from starlette.applications import Starlette
from starlette.concurrency import run_in_threadpool
from starlette.requests import Request
from starlette.responses import RedirectResponse, Response
from starlette.routing import Mount, Route
from starlette.staticfiles import StaticFiles
from starlette.templating import Jinja2Templates

from growcam import __version__
from growcam.application.ports import AnalyzerError, CameraError, Direction
from growcam.application.services import NoActiveGrowError, edited_seed
from growcam.container import Container
from growcam.domain.models import DiaryKind, MilestoneType, SeedInfo, SeedType
from growcam.scheduler import Scheduler
from growcam.web import views

log = logging.getLogger("growcam.web")

WEB_DIR = Path(__file__).parent
FONT_SIZES = {"normal": "100%", "gross": "115%", "sehr-gross": "130%"}
QUICK_DIARY = [DiaryKind.WATERED, DiaryKind.FERTILIZED, DiaryKind.REPOTTED, DiaryKind.TRAINED]

# Errors we can explain to the user instead of showing a crash page.
USER_ERRORS = (CameraError, AnalyzerError, NoActiveGrowError)


def create_app(container: Container) -> Starlette:
    templates = Jinja2Templates(directory=str(WEB_DIR / "templates"))
    env = templates.env
    env.globals.update(version=__version__, settings=container.settings)
    env.filters.update(
        date=views.fmt_date, short=views.fmt_short, time=views.fmt_time, tone=views.SEVERITY_TONE.get
    )

    c = container

    # ------------------------------------------------------------------ helpers

    def render(request: Request, template: str, tab: str, /, **context) -> Response:
        size = request.cookies.get("schrift", "normal")
        return templates.TemplateResponse(
            request,
            template,
            {
                "tab": tab,
                "font_size": size if size in FONT_SIZES else "normal",
                "font_scale": FONT_SIZES.get(size, "100%"),
                "grow": c.grows.active(),
                "today": c.clock.now().date(),
                "msg": request.query_params.get("msg", ""),
                "err": request.query_params.get("err", ""),
                **context,
            },
        )

    def back(to: str, **params: str) -> RedirectResponse:
        query = urlencode({k: v for k, v in params.items() if v})
        return RedirectResponse(f"{to}?{query}" if query else to, status_code=303)

    async def attempt(to: str, ok_message: str, func, *args) -> RedirectResponse:
        """Run a blocking service call; turn known errors into a friendly message."""
        try:
            await run_in_threadpool(func, *args)
        except USER_ERRORS as exc:
            return back(to, err=str(exc))
        return back(to, msg=ok_message)

    # ------------------------------------------------------------------ start

    async def home(request: Request) -> Response:
        grow = c.grows.active()
        if grow is None:
            return render(request, "welcome.html", "start", archived=c.grows.archived())
        analysis = c.monitoring.latest_analysis(grow.id)
        reports = c.reports.reports(grow.id)
        return render(
            request,
            "home.html",
            "start",
            analysis=analysis,
            snapshot=c.monitoring.latest_snapshot(grow.id),
            suggestion=grow.pending_suggestion(),
            report=reports[0] if reports else None,
            relative=views.relative_day,
        )

    async def analyze(request: Request) -> Response:
        form = await request.form()
        return await attempt("/", "Die KI hat sich die Pflanzen angesehen.", c.monitoring.analyze_now, form.get("note", ""))

    async def snapshot(request: Request) -> Response:
        return await attempt(request.query_params.get("next", "/"), "Neues Foto gemacht.", c.monitoring.take_snapshot)

    async def milestone_action(request: Request) -> Response:
        kind = MilestoneType(request.path_params["kind"])
        action = request.path_params["action"]
        func = {"confirm": c.grows.confirm_milestone, "reject": c.grows.reject_milestone, "undo": c.grows.undo_milestone}[action]
        text = {"confirm": "Gespeichert.", "reject": "Okay, die KI schaut weiter.", "undo": "Zurückgenommen."}[action]
        return await attempt(request.query_params.get("next", "/"), text, func, kind)

    async def image(request: Request) -> Response:
        data = await run_in_threadpool(c.monitoring.snapshot_image, request.path_params["snapshot_id"])
        if data is None:
            return Response(status_code=404)
        return Response(data, media_type="image/jpeg", headers={"Cache-Control": "max-age=86400"})

    # ------------------------------------------------------------------ camera

    async def camera_page(request: Request) -> Response:
        presets, analysis_preset, camera_error = [], None, ""
        try:
            presets = await run_in_threadpool(c.camera.presets)
            analysis_preset = await run_in_threadpool(c.camera.analysis_preset)
        except CameraError as exc:
            camera_error = str(exc)
        return render(
            request,
            "camera.html",
            "kamera",
            presets=presets,
            analysis_preset=analysis_preset,
            camera_error=camera_error,
        )

    async def live_frame(request: Request) -> Response:
        try:
            data = await run_in_threadpool(c.camera.live_frame)
        except CameraError:
            return Response(status_code=503)
        return Response(data, media_type="image/jpeg", headers={"Cache-Control": "no-store"})

    async def camera_move(request: Request) -> Response:
        direction = request.path_params["direction"]
        if direction not in Direction.ALL:
            return Response(status_code=404)
        return await attempt("/kamera", "", c.camera.move, direction)

    async def camera_preset(request: Request) -> Response:
        return await attempt("/kamera", "", c.camera.goto_preset, request.path_params["preset_id"])

    async def camera_remember(request: Request) -> Response:
        return await attempt("/kamera", "Gemerkt. Fotos werden ab jetzt aus dieser Ansicht gemacht.", c.camera.remember_analysis_position)

    # ------------------------------------------------------------------ diary

    async def diary(request: Request) -> Response:
        grow = c.grows.active()
        if grow is None:
            return back("/")
        return render(
            request,
            "diary.html",
            "tagebuch",
            milestones=views.milestone_rows(grow),
            entries=c.grows.diary(grow.id),
            quick=QUICK_DIARY,
        )

    async def diary_add(request: Request) -> Response:
        form = await request.form()
        try:
            kind = DiaryKind(form.get("kind", "note"))
        except ValueError:
            return back("/tagebuch")
        return await attempt("/tagebuch", f"Eingetragen: {kind.label}", c.grows.add_diary_entry, kind, form.get("text", ""))

    async def diary_delete(request: Request) -> Response:
        return await attempt("/tagebuch", "Eintrag gelöscht.", c.grows.delete_diary_entry, request.path_params["entry_id"])

    async def grow_finish(request: Request) -> Response:
        return await attempt("/archiv", "Der Grow ist abgeschlossen und im Archiv.", c.grows.finish)

    # ------------------------------------------------------------------ reports

    async def report_page(request: Request) -> Response:
        report = c.reports.get(request.path_params["report_id"])
        if report is None:
            return back("/")
        return render(
            request,
            "report.html",
            "start",
            report=report,
            reports=c.reports.reports(report.grow_id),
            report_grow=c.grows.get(report.grow_id),
        )

    async def report_generate(request: Request) -> Response:
        try:
            report = await run_in_threadpool(c.reports.generate_last_week_if_missing)
        except USER_ERRORS as exc:
            return back("/", err=str(exc))
        if report is None:
            return back("/", msg="Für die letzte Woche gibt es schon einen Bericht.")
        return back(f"/berichte/{report.id}")

    # ------------------------------------------------------------------ archive

    async def archive(request: Request) -> Response:
        return render(request, "archive.html", "archiv", archived=c.grows.archived())

    async def archive_detail(request: Request) -> Response:
        archived_grow = c.grows.get(request.path_params["grow_id"])
        if archived_grow is None:
            return back("/archiv")
        return render(
            request,
            "archive_detail.html",
            "archiv",
            item=archived_grow,
            milestones=views.milestone_rows(archived_grow),
            reports=c.reports.reports(archived_grow.id),
            last_snapshot=c.monitoring.latest_snapshot(archived_grow.id),
        )

    # ------------------------------------------------------------------ new grow wizard

    async def wizard_start(request: Request) -> Response:
        return render(request, "wizard_1.html", "tagebuch", step=1, name="", sow_date=c.clock.now().date())

    async def wizard_seeds(request: Request) -> Response:
        q = request.query_params
        query = q.get("q", "").strip()
        results, error = [], ""
        if query:
            try:
                results = await run_in_threadpool(c.seeds.search, query)
            except AnalyzerError as exc:
                error = str(exc)
        return render(
            request,
            "wizard_2.html",
            "tagebuch",
            step=2,
            name=q.get("name", ""),
            sow_date=q.get("date", ""),
            query=query,
            results=results,
            searched=bool(query),
            search_error=error,
        )

    async def wizard_confirm(request: Request) -> Response:
        q = request.query_params
        seed = SeedInfo(name="")
        query = q.get("q", "")
        if query and q.get("pick", "").isdigit():
            results = await run_in_threadpool(c.seeds.search, query)  # served from cache
            index = int(q["pick"])
            if index < len(results):
                seed = results[index]
        return render(
            request,
            "wizard_3.html",
            "tagebuch",
            step=3,
            name=q.get("name", ""),
            sow_date=q.get("date", ""),
            query=query,
            seed=seed,
            seed_types=list(SeedType),
        )

    async def grow_create(request: Request) -> Response:
        form = await request.form()
        try:
            sow_date = date.fromisoformat(form.get("sow_date", ""))
        except ValueError:
            sow_date = c.clock.now().date()
        seed_name = form.get("seed_name", "").strip()
        seed = None
        if seed_name:
            seed = edited_seed(
                SeedInfo(name=seed_name),
                seed_type=SeedType(form.get("seed_type", "unknown")),
                weeks_to_harvest=_int_or_none(form.get("weeks_to_harvest")),
                flowering_weeks=_int_or_none(form.get("flowering_weeks")),
                height=form.get("height", "").strip(),
                light_schedule=form.get("light_schedule", "").strip(),
                difficulty=form.get("difficulty", "").strip(),
            )
        plants = _int_or_none(form.get("plants")) or 1
        await run_in_threadpool(c.grows.start, form.get("name", ""), sow_date, plants, seed)
        return back("/", msg="Der neue Grow läuft! Die KI meldet sich, sobald Keimlinge zu sehen sind.")

    # ------------------------------------------------------------------ settings

    async def settings_page(request: Request) -> Response:
        return render(
            request,
            "settings.html",
            "einstellungen",
            update=c.updates.status(),
            auto_install=c.updates.auto_install(),
        )

    async def settings_update(request: Request) -> Response:
        action = request.path_params["action"]
        if action == "check":
            c.updates.check_now()
            return back("/einstellungen", msg="Suche nach Updates gestartet. Das dauert etwa eine Minute.")
        if action == "install":
            c.updates.install_now()
            return back("/einstellungen", msg="Update wird installiert. Die Seite ist gleich kurz weg.")
        if action == "auto":
            form = await request.form()
            c.updates.set_auto_install(form.get("enabled") == "1")
            return back("/einstellungen", msg="Gespeichert.")
        return Response(status_code=404)

    async def font_size(request: Request) -> Response:
        size = request.path_params["size"]
        target = request.headers.get("referer") or "/"
        response = RedirectResponse(target, status_code=303)
        if size in FONT_SIZES:
            response.set_cookie("schrift", size, max_age=60 * 60 * 24 * 365 * 5, samesite="lax")
        return response

    # ------------------------------------------------------------------ app

    routes = [
        Route("/", home),
        Route("/pruefen", analyze, methods=["POST"]),
        Route("/foto", snapshot, methods=["POST"]),
        Route("/bilder/{snapshot_id:int}.jpg", image),
        Route("/meilenstein/{kind}/{action:str}", milestone_action, methods=["POST"]),
        Route("/kamera", camera_page),
        Route("/kamera/live.jpg", live_frame),
        Route("/kamera/bewegen/{direction}", camera_move, methods=["POST"]),
        Route("/kamera/position/{preset_id}", camera_preset, methods=["POST"]),
        Route("/kamera/merken", camera_remember, methods=["POST"]),
        Route("/tagebuch", diary),
        Route("/tagebuch/eintrag", diary_add, methods=["POST"]),
        Route("/tagebuch/eintrag/{entry_id:int}/loeschen", diary_delete, methods=["POST"]),
        Route("/grow/beenden", grow_finish, methods=["POST"]),
        Route("/berichte/{report_id:int}", report_page),
        Route("/berichte/erstellen", report_generate, methods=["POST"]),
        Route("/archiv", archive),
        Route("/archiv/{grow_id:int}", archive_detail),
        Route("/neu", wizard_start),
        Route("/neu/samen", wizard_seeds),
        Route("/neu/pruefen", wizard_confirm),
        Route("/neu", grow_create, methods=["POST"]),
        Route("/einstellungen", settings_page),
        Route("/einstellungen/update/{action}", settings_update, methods=["POST"]),
        Route("/schrift/{size}", font_size, methods=["POST"]),
        Mount("/static", StaticFiles(directory=str(WEB_DIR / "static")), name="static"),
    ]

    scheduler = Scheduler(container)

    @contextlib.asynccontextmanager
    async def lifespan(app: Starlette):
        scheduler.start()
        yield
        await scheduler.stop()

    return Starlette(routes=routes, lifespan=lifespan)


def _int_or_none(value) -> int | None:
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return None
