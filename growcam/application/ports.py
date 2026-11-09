"""Ports (interfaces) the application layer depends on.

Infrastructure adapters implement these protocols; the services never import
concrete adapters. This keeps the use cases testable with simple fakes.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime
from typing import Protocol

from growcam.domain.models import (
    Analysis,
    DiaryEntry,
    Finding,
    Grow,
    Milestone,
    MilestoneType,
    SeedInfo,
    Snapshot,
    WeeklyReport,
)

# ---------------------------------------------------------------------------
# Data passed to / returned from the AI
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class GrowContext:
    """Everything the AI needs to know about the grow besides the images."""

    grow_name: str
    day_count: int
    phase: str
    seed: SeedInfo | None
    open_milestones: list[MilestoneType]
    recent_diary: list[str] = field(default_factory=list)
    previous_headline: str = ""
    user_note: str = ""


@dataclass(frozen=True)
class AnalysisResult:
    health_score: int
    headline: str
    findings: list[Finding]
    suggested_milestone: MilestoneType | None = None
    milestone_note: str = ""


@dataclass(frozen=True)
class ReportDraft:
    headline: str
    happened: list[str]
    next_steps: list[str]
    needs_attention: bool = False


class PlantAnalyzer(Protocol):
    """Vision model that looks at plant photos."""

    def analyze(self, image: bytes, previous_image: bytes | None, context: GrowContext) -> AnalysisResult: ...

    def weekly_report(self, images: list[bytes], context: GrowContext, analyses: list[str]) -> ReportDraft: ...


class SeedCatalog(Protocol):
    """Looks up strain data, e.g. via web search."""

    def search(self, query: str) -> list[SeedInfo]: ...


# ---------------------------------------------------------------------------
# Camera
# ---------------------------------------------------------------------------


class Direction:
    UP = "up"
    DOWN = "down"
    LEFT = "left"
    RIGHT = "right"
    ALL = (UP, DOWN, LEFT, RIGHT)


@dataclass(frozen=True)
class CameraPreset:
    id: str
    name: str


class Camera(Protocol):
    def capture(self) -> bytes:
        """Return a single JPEG frame."""
        ...

    def move(self, direction: str) -> None: ...

    def presets(self) -> list[CameraPreset]: ...

    def goto_preset(self, preset_id: str) -> None: ...

    def save_preset(self, name: str) -> CameraPreset: ...


class CameraError(RuntimeError):
    """Raised by camera adapters when the camera can't be reached."""


class AnalyzerError(RuntimeError):
    """Raised by AI adapters when the model call fails."""


# ---------------------------------------------------------------------------
# Persistence
# ---------------------------------------------------------------------------


class GrowRepository(Protocol):
    def active(self) -> Grow | None: ...

    def get(self, grow_id: int) -> Grow | None: ...

    def archived(self) -> list[Grow]: ...

    def add(self, grow: Grow) -> Grow: ...

    def archive(self, grow_id: int, on: date, summary: str) -> None: ...

    def set_milestone(self, grow_id: int, milestone: Milestone) -> None: ...

    def remove_milestone(self, grow_id: int, kind: MilestoneType) -> None: ...


class SnapshotRepository(Protocol):
    def add(self, snapshot: Snapshot) -> Snapshot: ...

    def get(self, snapshot_id: int) -> Snapshot | None: ...

    def latest(self, grow_id: int) -> Snapshot | None: ...

    def scheduled_between(self, grow_id: int, start: datetime, end: datetime) -> list[Snapshot]: ...

    def count(self, grow_id: int) -> int: ...


class AnalysisRepository(Protocol):
    def add(self, analysis: Analysis) -> Analysis: ...

    def latest(self, grow_id: int) -> Analysis | None: ...

    def between(self, grow_id: int, start: datetime, end: datetime) -> list[Analysis]: ...

    def average_score(self, grow_id: int) -> float | None: ...


class DiaryRepository(Protocol):
    def add(self, entry: DiaryEntry) -> DiaryEntry: ...

    def recent(self, grow_id: int, limit: int = 20) -> list[DiaryEntry]: ...

    def between(self, grow_id: int, start: datetime, end: datetime) -> list[DiaryEntry]: ...

    def delete(self, entry_id: int) -> None: ...


class ReportRepository(Protocol):
    def add(self, report: WeeklyReport) -> WeeklyReport: ...

    def for_grow(self, grow_id: int) -> list[WeeklyReport]: ...

    def get(self, report_id: int) -> WeeklyReport | None: ...

    def exists(self, grow_id: int, week_start: date) -> bool: ...


class SeedCache(Protocol):
    def get(self, query: str) -> list[SeedInfo] | None: ...

    def put(self, query: str, seeds: list[SeedInfo]) -> None: ...


class SettingsStore(Protocol):
    def get(self, key: str, default: str = "") -> str: ...

    def set(self, key: str, value: str) -> None: ...


class ImageStore(Protocol):
    def save(self, grow_id: int, taken_at: datetime, data: bytes) -> str:
        """Persist a JPEG and return its relative path."""
        ...

    def load(self, path: str) -> bytes: ...


class Clock(Protocol):
    def now(self) -> datetime: ...
