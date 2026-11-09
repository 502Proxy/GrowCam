"""Domain entities and value objects.

Everything here is plain Python without any framework or I/O dependency,
so the rules can be tested in isolation.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
from enum import Enum


class SeedType(str, Enum):
    """How a strain starts flowering."""

    AUTOFLOWER = "autoflower"
    PHOTOPERIOD = "photoperiod"
    UNKNOWN = "unknown"


class MilestoneType(str, Enum):
    """Stages of a grow, in chronological order."""

    SOWN = "sown"
    SPROUTED = "sprouted"
    VEGETATIVE = "vegetative"
    FLOWERING = "flowering"
    HARVEST = "harvest"

    @property
    def label(self) -> str:
        return _MILESTONE_LABELS[self]

    @property
    def order(self) -> int:
        return list(MilestoneType).index(self)


_MILESTONE_LABELS = {
    MilestoneType.SOWN: "Samen eingepflanzt",
    MilestoneType.SPROUTED: "Keimling ist da",
    MilestoneType.VEGETATIVE: "Wachstumsphase",
    MilestoneType.FLOWERING: "Erste Blüten",
    MilestoneType.HARVEST: "Ernte",
}


class MilestoneStatus(str, Enum):
    CONFIRMED = "confirmed"  # reached, confirmed by the user
    SUGGESTED = "suggested"  # the AI thinks it is reached, waiting for the user
    REJECTED = "rejected"  # the user said "not yet"; AI keeps watching


class Phase(str, Enum):
    GERMINATION = "Keimung"
    SEEDLING = "Keimling"
    VEGETATIVE = "Wachstumsphase"
    FLOWERING = "Blütephase"
    HARVESTED = "Geerntet"


_PHASE_BY_MILESTONE = {
    MilestoneType.SOWN: Phase.GERMINATION,
    MilestoneType.SPROUTED: Phase.SEEDLING,
    MilestoneType.VEGETATIVE: Phase.VEGETATIVE,
    MilestoneType.FLOWERING: Phase.FLOWERING,
    MilestoneType.HARVEST: Phase.HARVESTED,
}


class Severity(str, Enum):
    """How urgent an AI finding is. Kept to three levels on purpose."""

    OK = "ok"
    MINOR = "minor"
    WATCH = "watch"

    @property
    def label(self) -> str:
        return {"ok": "Alles gut", "minor": "Kleinigkeit", "watch": "Im Auge behalten"}[self.value]


class DiaryKind(str, Enum):
    WATERED = "watered"
    FERTILIZED = "fertilized"
    REPOTTED = "repotted"
    TRAINED = "trained"
    NOTE = "note"

    @property
    def label(self) -> str:
        return {
            "watered": "Gegossen",
            "fertilized": "Gedüngt",
            "repotted": "Umgetopft",
            "trained": "Training",
            "note": "Notiz",
        }[self.value]


@dataclass(frozen=True)
class SeedInfo:
    """Strain data, either looked up on the web or entered by hand."""

    name: str
    seed_type: SeedType = SeedType.UNKNOWN
    weeks_to_harvest: int | None = None  # sowing -> harvest (autoflowers)
    flowering_weeks: int | None = None  # flip to 12/12 -> harvest (photoperiods)
    height: str = ""
    light_schedule: str = ""
    difficulty: str = ""

    @property
    def type_label(self) -> str:
        return {
            SeedType.AUTOFLOWER: "Autoflower – blüht von selbst",
            SeedType.PHOTOPERIOD: "Photoperiodisch – Licht muss für die Blüte umgestellt werden",
            SeedType.UNKNOWN: "unbekannt",
        }[self.seed_type]


@dataclass
class Milestone:
    type: MilestoneType
    status: MilestoneStatus
    reached_on: date
    note: str = ""
    by_ai: bool = False


@dataclass
class Grow:
    id: int | None
    name: str
    sow_date: date
    plant_count: int
    seed: SeedInfo | None = None
    archived_on: date | None = None
    summary: str = ""
    milestones: list[Milestone] = field(default_factory=list)

    # ---- derived state -------------------------------------------------

    @property
    def is_active(self) -> bool:
        return self.archived_on is None

    def day_count(self, today: date) -> int:
        return max(0, (today - self.sow_date).days)

    def milestone(self, kind: MilestoneType) -> Milestone | None:
        return next((m for m in self.milestones if m.type == kind), None)

    def is_reached(self, kind: MilestoneType) -> bool:
        if kind == MilestoneType.SOWN:
            return True
        m = self.milestone(kind)
        return m is not None and m.status == MilestoneStatus.CONFIRMED

    def phase(self) -> Phase:
        reached = [k for k in MilestoneType if self.is_reached(k)]
        return _PHASE_BY_MILESTONE[reached[-1]]

    def pending_suggestion(self) -> Milestone | None:
        """The AI suggestion the user still has to confirm, if any."""
        return next((m for m in self.milestones if m.status == MilestoneStatus.SUGGESTED), None)

    def open_milestones(self) -> list[MilestoneType]:
        """Milestones the AI may still detect (not yet confirmed)."""
        return [k for k in MilestoneType if k != MilestoneType.SOWN and not self.is_reached(k)]

    def expected_date(self, kind: MilestoneType) -> date | None:
        """Rough expectation used for display until the milestone is reached."""
        offsets = {MilestoneType.SPROUTED: 4, MilestoneType.VEGETATIVE: 15}
        if kind in offsets:
            return self.sow_date + timedelta(days=offsets[kind])
        if self.seed and self.seed.seed_type == SeedType.AUTOFLOWER:
            if kind == MilestoneType.FLOWERING:
                return self.sow_date + timedelta(days=28)
            if kind == MilestoneType.HARVEST and self.seed.weeks_to_harvest:
                return self.sow_date + timedelta(weeks=self.seed.weeks_to_harvest)
        if kind == MilestoneType.HARVEST and self.seed and self.seed.flowering_weeks:
            flowering = self.milestone(MilestoneType.FLOWERING)
            if flowering and flowering.status == MilestoneStatus.CONFIRMED:
                return flowering.reached_on + timedelta(weeks=self.seed.flowering_weeks)
        return None


@dataclass
class Snapshot:
    id: int | None
    grow_id: int
    taken_at: datetime
    path: str  # relative to the image root
    scheduled: bool = True


@dataclass(frozen=True)
class Finding:
    severity: Severity
    title: str
    advice: str


@dataclass
class Analysis:
    id: int | None
    grow_id: int
    snapshot_id: int
    created_at: datetime
    health_score: int
    headline: str
    findings: list[Finding]
    suggested_milestone: MilestoneType | None = None
    milestone_note: str = ""

    @property
    def needs_attention(self) -> bool:
        return any(f.severity == Severity.WATCH for f in self.findings)


@dataclass
class DiaryEntry:
    id: int | None
    grow_id: int
    created_at: datetime
    kind: DiaryKind
    text: str = ""


@dataclass
class WeeklyReport:
    id: int | None
    grow_id: int
    week_start: date  # Monday
    headline: str
    happened: list[str]
    done: list[str]
    next_steps: list[str]
    needs_attention: bool = False
    snapshot_ids: list[int] = field(default_factory=list)

    @property
    def week_end(self) -> date:
        return self.week_start + timedelta(days=6)

    @property
    def calendar_week(self) -> int:
        return self.week_start.isocalendar().week
