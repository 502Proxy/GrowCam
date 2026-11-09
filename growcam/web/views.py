"""Presentation helpers: turn domain objects into simple template data."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime

from growcam.domain.models import Grow, MilestoneStatus, MilestoneType, Severity

WEEKDAYS = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"]


def fmt_date(value: date | datetime | None) -> str:
    return value.strftime("%d.%m.%Y") if value else ""


def fmt_short(value: date | datetime | None) -> str:
    return f"{WEEKDAYS[value.weekday()]}, {value:%d.%m.}" if value else ""


def fmt_time(value: datetime | None) -> str:
    return value.strftime("%H:%M") if value else ""


def relative_day(value: datetime, today: date) -> str:
    days = (today - value.date()).days
    if days == 0:
        return f"Heute, {value:%H:%M} Uhr"
    if days == 1:
        return f"Gestern, {value:%H:%M} Uhr"
    return f"{fmt_short(value)} {value:%H:%M} Uhr"


SEVERITY_TONE = {Severity.OK: "ok", Severity.MINOR: "info", Severity.WATCH: "warn"}


@dataclass(frozen=True)
class MilestoneRow:
    kind: str
    title: str
    date_text: str
    source: str
    tone: str  # ok | info | warn | muted
    dot: str  # done | pending | open


def milestone_rows(grow: Grow) -> list[MilestoneRow]:
    rows = []
    for kind in MilestoneType:
        m = grow.milestone(kind)
        if kind == MilestoneType.SOWN:
            rows.append(MilestoneRow(kind.value, kind.label, fmt_date(grow.sow_date), "Von dir eingetragen", "muted", "done"))
        elif m and m.status == MilestoneStatus.CONFIRMED:
            source = "Von der KI erkannt, von dir bestätigt" if m.by_ai else "Von dir eingetragen"
            rows.append(MilestoneRow(kind.value, kind.label, fmt_date(m.reached_on), source, "ok", "done"))
        elif m and m.status == MilestoneStatus.SUGGESTED:
            rows.append(
                MilestoneRow(kind.value, kind.label, fmt_date(m.reached_on), "Die KI vermutet es – bitte bestätigen", "warn", "pending")
            )
        else:
            expected = grow.expected_date(kind)
            text = f"erwartet etwa {fmt_date(expected)}" if expected else "noch offen"
            rows.append(MilestoneRow(kind.value, kind.label, text, "Die KI schaut jeden Tag nach", "muted", "open"))
    return rows
