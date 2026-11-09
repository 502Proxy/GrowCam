"""Shared fixtures: a fully wired service stack on a temporary SQLite DB with fakes."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime

import pytest

from growcam.application.ports import AnalysisResult, GrowContext, ReportDraft
from growcam.application.services import CameraService, GrowService, MonitoringService, ReportService
from growcam.domain.models import Finding, MilestoneType, Severity
from growcam.infrastructure.camera import DemoCamera
from growcam.infrastructure.files import FileImageStore
from growcam.infrastructure.sqlite_store import (
    Database,
    SqliteAnalysisRepository,
    SqliteDiaryRepository,
    SqliteGrowRepository,
    SqliteReportRepository,
    SqliteSettingsStore,
    SqliteSnapshotRepository,
)


class FakeClock:
    def __init__(self, now: datetime) -> None:
        self.current = now

    def now(self) -> datetime:
        return self.current


class FakeAnalyzer:
    """Returns whatever the test configures and records the contexts it got."""

    def __init__(self) -> None:
        self.suggest: MilestoneType | None = None
        self.contexts: list[GrowContext] = []
        self.report_images: list[bytes] = []

    def analyze(self, image, previous_image, context):
        self.contexts.append(context)
        return AnalysisResult(
            health_score=150,  # out of range on purpose: the service must clamp it
            headline="Alles gut",
            findings=[Finding(Severity.OK, f"Befund {i}", "nichts") for i in range(5)],
            suggested_milestone=self.suggest,
            milestone_note="gesehen",
        )

    def weekly_report(self, images, context, analyses):
        self.report_images = images
        return ReportDraft("Gute Woche", ["wuchs"], ["weiter so"], False)


@dataclass
class Stack:
    clock: FakeClock
    analyzer: FakeAnalyzer
    grows: GrowService
    monitoring: MonitoringService
    reports: ReportService
    camera: CameraService
    grow_repo: SqliteGrowRepository


@pytest.fixture
def stack(tmp_path) -> Stack:
    db = Database(tmp_path / "test.db")
    grow_repo = SqliteGrowRepository(db)
    snapshots = SqliteSnapshotRepository(db)
    analyses = SqliteAnalysisRepository(db)
    diary = SqliteDiaryRepository(db)
    reports = SqliteReportRepository(db)
    settings = SqliteSettingsStore(db)
    images = FileImageStore(tmp_path / "images")
    clock = FakeClock(datetime(2026, 11, 9, 12, 0))
    camera = DemoCamera()
    camera_service = CameraService(camera, settings)
    analyzer = FakeAnalyzer()
    return Stack(
        clock=clock,
        analyzer=analyzer,
        grows=GrowService(grow_repo, diary, reports, clock),
        monitoring=MonitoringService(
            grow_repo, snapshots, analyses, diary, images, camera, camera_service, analyzer, clock, settle_seconds=0
        ),
        reports=ReportService(grow_repo, snapshots, analyses, diary, reports, images, analyzer, clock),
        camera=camera_service,
        grow_repo=grow_repo,
    )
