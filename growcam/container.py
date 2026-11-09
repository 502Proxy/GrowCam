"""Composition root: the only place that wires concrete adapters to services."""

from __future__ import annotations

from dataclasses import dataclass

from growcam import __version__
from growcam.application.services import (
    CameraService,
    GrowService,
    MonitoringService,
    ReportService,
    SeedService,
)
from growcam.config import PROJECT_DIR, Settings
from growcam.infrastructure.camera import DemoCamera, TapoCamera
from growcam.infrastructure.claude_ai import ClaudeClient, ClaudePlantAnalyzer, ClaudeSeedCatalog
from growcam.infrastructure.demo_ai import DemoPlantAnalyzer, DemoSeedCatalog
from growcam.infrastructure.files import FileImageStore, SystemClock
from growcam.infrastructure.sqlite_store import (
    Database,
    SqliteAnalysisRepository,
    SqliteDiaryRepository,
    SqliteGrowRepository,
    SqliteReportRepository,
    SqliteSeedCache,
    SqliteSettingsStore,
    SqliteSnapshotRepository,
)
from growcam.infrastructure.updates import UpdateManager


@dataclass
class Container:
    settings: Settings
    grows: GrowService
    camera: CameraService
    monitoring: MonitoringService
    reports: ReportService
    seeds: SeedService
    updates: UpdateManager
    state: SqliteSettingsStore  # small key/value store, also used by the scheduler
    clock: SystemClock


def build_container(settings: Settings) -> Container:
    db = Database(settings.data_dir / "growcam.db")
    grow_repo = SqliteGrowRepository(db)
    snapshot_repo = SqliteSnapshotRepository(db)
    analysis_repo = SqliteAnalysisRepository(db)
    diary_repo = SqliteDiaryRepository(db)
    report_repo = SqliteReportRepository(db)
    state = SqliteSettingsStore(db)
    images = FileImageStore(settings.data_dir / "images")
    clock = SystemClock()

    if settings.demo_camera:
        camera = DemoCamera()
    else:
        camera = TapoCamera(
            settings.tapo_host,
            settings.tapo_rtsp_user,
            settings.tapo_rtsp_password,
            settings.tapo_cloud_password,
            settings.ffmpeg,
        )

    if settings.demo_ai:
        analyzer, catalog = DemoPlantAnalyzer(), DemoSeedCatalog()
    else:
        client = ClaudeClient(settings.anthropic_api_key, settings.anthropic_model, settings.anthropic_version)
        analyzer, catalog = ClaudePlantAnalyzer(client), ClaudeSeedCatalog(client)

    camera_service = CameraService(camera, state)
    return Container(
        settings=settings,
        grows=GrowService(grow_repo, diary_repo, report_repo, clock),
        camera=camera_service,
        monitoring=MonitoringService(
            grow_repo,
            snapshot_repo,
            analysis_repo,
            diary_repo,
            images,
            camera,
            camera_service,
            analyzer,
            clock,
            settle_seconds=0 if settings.demo_camera else 5,
        ),
        reports=ReportService(
            grow_repo, snapshot_repo, analysis_repo, diary_repo, report_repo, images, analyzer, clock
        ),
        seeds=SeedService(catalog, SqliteSeedCache(db)),
        updates=UpdateManager(settings.data_dir, PROJECT_DIR, __version__),
        state=state,
        clock=clock,
    )
