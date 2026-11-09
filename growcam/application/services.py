"""Use cases. Each service has one responsibility and talks only to ports."""

from __future__ import annotations

import threading
import time
from collections import Counter
from dataclasses import replace
from datetime import date, datetime, time as dtime, timedelta

from growcam.application.ports import (
    AnalysisRepository,
    Camera,
    CameraPreset,
    Clock,
    DiaryRepository,
    GrowContext,
    GrowRepository,
    ImageStore,
    PlantAnalyzer,
    ReportRepository,
    SeedCache,
    SeedCatalog,
    SettingsStore,
    SnapshotRepository,
)
from growcam.domain.models import (
    Analysis,
    DiaryEntry,
    DiaryKind,
    Grow,
    Milestone,
    MilestoneStatus,
    MilestoneType,
    SeedInfo,
    Snapshot,
    WeeklyReport,
)


class NoActiveGrowError(RuntimeError):
    """Raised when a use case needs a running grow but none exists."""


# A rejected AI suggestion may only come back after this many days.
REJECTION_COOLDOWN_DAYS = 2


class GrowService:
    """Starting, archiving and documenting grows."""

    def __init__(
        self,
        grows: GrowRepository,
        diary: DiaryRepository,
        reports: ReportRepository,
        clock: Clock,
    ) -> None:
        self._grows = grows
        self._diary = diary
        self._reports = reports
        self._clock = clock

    def active(self) -> Grow | None:
        return self._grows.active()

    def require_active(self) -> Grow:
        grow = self._grows.active()
        if grow is None:
            raise NoActiveGrowError("Es läuft gerade kein Grow.")
        return grow

    def archived(self) -> list[Grow]:
        return self._grows.archived()

    def get(self, grow_id: int) -> Grow | None:
        return self._grows.get(grow_id)

    def start(self, name: str, sow_date: date, plant_count: int, seed: SeedInfo | None) -> Grow:
        """Archive the running grow (if any) and start a new one."""
        today = self._clock.now().date()
        current = self._grows.active()
        if current is not None:
            self._grows.archive(current.id, today, self._summary_for(current))
        grow = Grow(
            id=None,
            name=name.strip() or "Neuer Grow",
            sow_date=sow_date,
            plant_count=max(1, plant_count),
            seed=seed,
        )
        return self._grows.add(grow)

    def finish(self) -> None:
        """Archive the running grow without starting a new one (after harvest)."""
        grow = self.require_active()
        today = self._clock.now().date()
        self._grows.set_milestone(grow.id, Milestone(MilestoneType.HARVEST, MilestoneStatus.CONFIRMED, today))
        self._grows.archive(grow.id, today, self._summary_for(grow))

    def confirm_milestone(self, kind: MilestoneType, on: date | None = None) -> None:
        """Confirm a milestone; earlier, unconfirmed milestones are implied."""
        grow = self.require_active()
        suggested = grow.milestone(kind)
        reached_on = on or (suggested.reached_on if suggested else self._clock.now().date())
        for earlier in MilestoneType:
            if earlier == MilestoneType.SOWN or earlier.order > kind.order:
                continue
            if not grow.is_reached(earlier):
                existing = grow.milestone(earlier)
                self._grows.set_milestone(
                    grow.id,
                    Milestone(
                        earlier,
                        MilestoneStatus.CONFIRMED,
                        reached_on,
                        note=existing.note if existing else "",
                        by_ai=existing.by_ai if existing else False,
                    ),
                )

    def reject_milestone(self, kind: MilestoneType) -> None:
        grow = self.require_active()
        self._grows.set_milestone(
            grow.id, Milestone(kind, MilestoneStatus.REJECTED, self._clock.now().date())
        )

    def undo_milestone(self, kind: MilestoneType) -> None:
        grow = self.require_active()
        self._grows.remove_milestone(grow.id, kind)

    def add_diary_entry(self, kind: DiaryKind, text: str = "") -> DiaryEntry:
        grow = self.require_active()
        entry = DiaryEntry(None, grow.id, self._clock.now(), kind, text.strip())
        return self._diary.add(entry)

    def delete_diary_entry(self, entry_id: int) -> None:
        self._diary.delete(entry_id)

    def diary(self, grow_id: int, limit: int = 30) -> list[DiaryEntry]:
        return self._diary.recent(grow_id, limit)

    def _summary_for(self, grow: Grow) -> str:
        reports = self._reports.for_grow(grow.id)
        return reports[0].headline if reports else ""


class CameraService:
    """Manual camera control and the stored 'analysis position'."""

    ANALYSIS_PRESET_KEY = "analysis_preset"
    LIVE_FRAME_TTL_SECONDS = 2.0

    def __init__(self, camera: Camera, settings: SettingsStore) -> None:
        self._camera = camera
        self._settings = settings
        self._live_lock = threading.Lock()
        self._live_frame: tuple[float, bytes] | None = None

    def live_frame(self) -> bytes:
        """Fresh frame for the live view; cached briefly so several viewers share one capture."""
        with self._live_lock:
            if self._live_frame and time.monotonic() - self._live_frame[0] < self.LIVE_FRAME_TTL_SECONDS:
                return self._live_frame[1]
            frame = self._camera.capture()
            self._live_frame = (time.monotonic(), frame)
            return frame

    def move(self, direction: str) -> None:
        self._camera.move(direction)
        self._invalidate_live_frame()

    def presets(self) -> list[CameraPreset]:
        return self._camera.presets()

    def goto_preset(self, preset_id: str) -> None:
        self._camera.goto_preset(preset_id)
        self._invalidate_live_frame()

    def save_preset(self, name: str) -> CameraPreset:
        return self._camera.save_preset(name)

    def analysis_preset(self) -> CameraPreset | None:
        preset_id = self._settings.get(self.ANALYSIS_PRESET_KEY)
        return next((p for p in self.presets() if p.id == preset_id), None) if preset_id else None

    def remember_analysis_position(self) -> CameraPreset:
        """Store the current view as a camera preset and use it for scheduled photos."""
        preset = self._camera.save_preset("GrowCam Analyse")
        self._settings.set(self.ANALYSIS_PRESET_KEY, preset.id)
        return preset

    def go_to_analysis_position(self) -> bool:
        preset_id = self._settings.get(self.ANALYSIS_PRESET_KEY)
        if not preset_id:
            return False
        self.goto_preset(preset_id)
        return True

    def _invalidate_live_frame(self) -> None:
        with self._live_lock:
            self._live_frame = None


class MonitoringService:
    """Taking photos and letting the AI look at them."""

    def __init__(
        self,
        grows: GrowRepository,
        snapshots: SnapshotRepository,
        analyses: AnalysisRepository,
        diary: DiaryRepository,
        images: ImageStore,
        camera: Camera,
        camera_service: CameraService,
        analyzer: PlantAnalyzer,
        clock: Clock,
        settle_seconds: float = 5.0,
    ) -> None:
        self._grows = grows
        self._snapshots = snapshots
        self._analyses = analyses
        self._diary = diary
        self._images = images
        self._camera = camera
        self._camera_service = camera_service
        self._analyzer = analyzer
        self._clock = clock
        self._settle_seconds = settle_seconds
        self._lock = threading.Lock()  # one camera/AI job at a time

    def take_snapshot(self, scheduled: bool = False) -> Snapshot:
        grow = self._require_active()
        with self._lock:
            return self._capture(grow, scheduled)

    def analyze_now(self, user_note: str = "") -> Analysis:
        """Move to the analysis position, take a photo and analyze it."""
        grow = self._require_active()
        with self._lock:
            if self._camera_service.go_to_analysis_position() and self._settle_seconds:
                time.sleep(self._settle_seconds)  # let the motor stop before capturing
            snapshot = self._capture(grow, scheduled=True)
            image = self._images.load(snapshot.path)

            previous = self._analyses.latest(grow.id)
            previous_image = None
            if previous is not None:
                prev_snapshot = self._snapshots.get(previous.snapshot_id)
                if prev_snapshot is not None:
                    previous_image = self._images.load(prev_snapshot.path)

            context = build_context(
                grow,
                self._clock.now(),
                self._diary.recent(grow.id, 10),
                previous.headline if previous else "",
                user_note,
            )
            result = self._analyzer.analyze(image, previous_image, context)
            analysis = self._analyses.add(
                Analysis(
                    id=None,
                    grow_id=grow.id,
                    snapshot_id=snapshot.id,
                    created_at=self._clock.now(),
                    health_score=max(0, min(100, result.health_score)),
                    headline=result.headline,
                    findings=list(result.findings)[:3],
                    suggested_milestone=result.suggested_milestone,
                    milestone_note=result.milestone_note,
                )
            )
            self._apply_milestone_suggestion(grow, analysis)
            return analysis

    def latest_analysis(self, grow_id: int) -> Analysis | None:
        return self._analyses.latest(grow_id)

    def latest_snapshot(self, grow_id: int) -> Snapshot | None:
        return self._snapshots.latest(grow_id)

    def snapshot_image(self, snapshot_id: int) -> bytes | None:
        snapshot = self._snapshots.get(snapshot_id)
        return self._images.load(snapshot.path) if snapshot else None

    def _capture(self, grow: Grow, scheduled: bool) -> Snapshot:
        taken_at = self._clock.now()
        data = self._camera.capture()
        path = self._images.save(grow.id, taken_at, data)
        return self._snapshots.add(Snapshot(None, grow.id, taken_at, path, scheduled))

    def _apply_milestone_suggestion(self, grow: Grow, analysis: Analysis) -> None:
        kind = analysis.suggested_milestone
        if kind is None or kind not in grow.open_milestones():
            return
        existing = grow.milestone(kind)
        today = analysis.created_at.date()
        if existing is not None:
            if existing.status == MilestoneStatus.SUGGESTED:
                return
            if existing.status == MilestoneStatus.REJECTED and (
                today - existing.reached_on
            ).days < REJECTION_COOLDOWN_DAYS:
                return
        self._grows.set_milestone(
            grow.id,
            Milestone(kind, MilestoneStatus.SUGGESTED, today, analysis.milestone_note, by_ai=True),
        )

    def _require_active(self) -> Grow:
        grow = self._grows.active()
        if grow is None:
            raise NoActiveGrowError("Es läuft gerade kein Grow.")
        return grow


class ReportService:
    """Weekly reports: one per calendar week, generated on Sunday evening."""

    def __init__(
        self,
        grows: GrowRepository,
        snapshots: SnapshotRepository,
        analyses: AnalysisRepository,
        diary: DiaryRepository,
        reports: ReportRepository,
        images: ImageStore,
        analyzer: PlantAnalyzer,
        clock: Clock,
    ) -> None:
        self._grows = grows
        self._snapshots = snapshots
        self._analyses = analyses
        self._diary = diary
        self._reports = reports
        self._images = images
        self._analyzer = analyzer
        self._clock = clock

    def reports(self, grow_id: int) -> list[WeeklyReport]:
        return self._reports.for_grow(grow_id)

    def get(self, report_id: int) -> WeeklyReport | None:
        return self._reports.get(report_id)

    def generate_for_week(self, week_start: date) -> WeeklyReport | None:
        grow = self._grows.active()
        if grow is None or self._reports.exists(grow.id, week_start):
            return None
        start = datetime.combine(week_start, dtime.min)
        end = start + timedelta(days=7)

        snapshots = one_per_day(self._snapshots.scheduled_between(grow.id, start, end))
        analyses = self._analyses.between(grow.id, start, end)
        entries = self._diary.between(grow.id, start, end)
        context = build_context(grow, self._clock.now(), entries, "", "")

        draft = self._analyzer.weekly_report(
            [self._images.load(s.path) for s in snapshots],
            context,
            [f"{a.created_at:%a %d.%m.}: {a.headline}" for a in analyses],
        )
        report = WeeklyReport(
            id=None,
            grow_id=grow.id,
            week_start=week_start,
            headline=draft.headline,
            happened=draft.happened,
            done=summarize_diary(entries),
            next_steps=draft.next_steps,
            needs_attention=draft.needs_attention,
            snapshot_ids=[s.id for s in snapshots],
        )
        return self._reports.add(report)

    def generate_last_week_if_missing(self) -> WeeklyReport | None:
        """Report for the week that ends today (Sunday) or ended last Sunday."""
        today = self._clock.now().date()
        week_start = today - timedelta(days=today.weekday())
        if today.weekday() != 6:  # not Sunday: the current week isn't over yet
            week_start -= timedelta(days=7)
        return self.generate_for_week(week_start)


class SeedService:
    """Strain lookup with a local cache so every strain is only searched once."""

    def __init__(self, catalog: SeedCatalog, cache: SeedCache) -> None:
        self._catalog = catalog
        self._cache = cache

    def search(self, query: str) -> list[SeedInfo]:
        key = " ".join(query.lower().split())
        if not key:
            return []
        cached = self._cache.get(key)
        if cached is not None:
            return cached
        results = self._catalog.search(query.strip())
        if results:
            self._cache.put(key, results)
        return results


# ---------------------------------------------------------------------------
# Small pure helpers (shared by services, easy to unit test)
# ---------------------------------------------------------------------------


def build_context(
    grow: Grow,
    now: datetime,
    diary: list[DiaryEntry],
    previous_headline: str,
    user_note: str,
) -> GrowContext:
    return GrowContext(
        grow_name=grow.name,
        day_count=grow.day_count(now.date()),
        phase=grow.phase().value,
        seed=grow.seed,
        open_milestones=grow.open_milestones(),
        recent_diary=[
            f"{e.created_at:%d.%m.} {e.kind.label}" + (f": {e.text}" if e.text else "") for e in diary
        ],
        previous_headline=previous_headline,
        user_note=user_note.strip(),
    )


def one_per_day(snapshots: list[Snapshot], preferred_hour: int = 12) -> list[Snapshot]:
    """Pick the snapshot closest to the preferred hour for each day."""
    best: dict[date, Snapshot] = {}
    for snap in snapshots:
        day = snap.taken_at.date()
        current = best.get(day)
        if current is None or abs(snap.taken_at.hour - preferred_hour) < abs(current.taken_at.hour - preferred_hour):
            best[day] = snap
    return [best[d] for d in sorted(best)]


def summarize_diary(entries: list[DiaryEntry]) -> list[str]:
    """'2× Gegossen', '1× Gedüngt' … – notes are listed as they are."""
    counts = Counter(e.kind for e in entries if e.kind != DiaryKind.NOTE)
    lines = [f"{n}× {kind.label.lower()}" for kind, n in counts.most_common()]
    lines += [e.text for e in entries if e.kind == DiaryKind.NOTE and e.text]
    return lines


def edited_seed(seed: SeedInfo, **changes: object) -> SeedInfo:
    """Return a copy of a looked-up seed with the user's corrections applied."""
    return replace(seed, **{k: v for k, v in changes.items() if v not in (None, "")})
