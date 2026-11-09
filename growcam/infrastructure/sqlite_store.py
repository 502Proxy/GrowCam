"""SQLite persistence (stdlib only).

A new connection is opened per operation, which keeps the repositories
thread-safe without a connection pool. SQLite handles this easily at the
volume this app produces (a few writes per hour).
"""

from __future__ import annotations

import json
import sqlite3
from contextlib import contextmanager
from dataclasses import asdict
from datetime import date, datetime
from pathlib import Path
from typing import Iterator

from growcam.domain.models import (
    Analysis,
    DiaryEntry,
    DiaryKind,
    Finding,
    Grow,
    Milestone,
    MilestoneStatus,
    MilestoneType,
    SeedInfo,
    SeedType,
    Severity,
    Snapshot,
    WeeklyReport,
)

SCHEMA = """
CREATE TABLE IF NOT EXISTS grows (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    sow_date TEXT NOT NULL,
    plant_count INTEGER NOT NULL,
    seed_json TEXT,
    archived_on TEXT,
    summary TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS milestones (
    grow_id INTEGER NOT NULL REFERENCES grows(id),
    type TEXT NOT NULL,
    status TEXT NOT NULL,
    reached_on TEXT NOT NULL,
    note TEXT NOT NULL DEFAULT '',
    by_ai INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (grow_id, type)
);
CREATE TABLE IF NOT EXISTS snapshots (
    id INTEGER PRIMARY KEY,
    grow_id INTEGER NOT NULL REFERENCES grows(id),
    taken_at TEXT NOT NULL,
    path TEXT NOT NULL,
    scheduled INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_snapshots_grow_time ON snapshots(grow_id, taken_at);
CREATE TABLE IF NOT EXISTS analyses (
    id INTEGER PRIMARY KEY,
    grow_id INTEGER NOT NULL REFERENCES grows(id),
    snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
    created_at TEXT NOT NULL,
    health_score INTEGER NOT NULL,
    headline TEXT NOT NULL,
    findings_json TEXT NOT NULL,
    suggested_milestone TEXT,
    milestone_note TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS diary (
    id INTEGER PRIMARY KEY,
    grow_id INTEGER NOT NULL REFERENCES grows(id),
    created_at TEXT NOT NULL,
    kind TEXT NOT NULL,
    text TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS reports (
    id INTEGER PRIMARY KEY,
    grow_id INTEGER NOT NULL REFERENCES grows(id),
    week_start TEXT NOT NULL,
    headline TEXT NOT NULL,
    body_json TEXT NOT NULL,
    needs_attention INTEGER NOT NULL DEFAULT 0,
    UNIQUE (grow_id, week_start)
);
CREATE TABLE IF NOT EXISTS seed_cache (
    query TEXT PRIMARY KEY,
    seeds_json TEXT NOT NULL,
    cached_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
"""


class Database:
    def __init__(self, path: Path) -> None:
        self._path = path
        path.parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as con:
            con.executescript(SCHEMA)

    @contextmanager
    def connect(self) -> Iterator[sqlite3.Connection]:
        con = sqlite3.connect(self._path, timeout=10)
        con.row_factory = sqlite3.Row
        con.execute("PRAGMA foreign_keys = ON")
        try:
            yield con
            con.commit()
        finally:
            con.close()


# ---------------------------------------------------------------------------
# (De)serialization helpers
# ---------------------------------------------------------------------------


def _seed_to_json(seed: SeedInfo | None) -> str | None:
    if seed is None:
        return None
    data = asdict(seed)
    data["seed_type"] = seed.seed_type.value
    return json.dumps(data, ensure_ascii=False)


def seed_from_dict(data: dict) -> SeedInfo:
    known = {k: data[k] for k in SeedInfo.__dataclass_fields__ if k in data}
    known["seed_type"] = SeedType(known.get("seed_type", "unknown"))
    return SeedInfo(**known)


def _seed_from_json(raw: str | None) -> SeedInfo | None:
    return seed_from_dict(json.loads(raw)) if raw else None


def _findings_to_json(findings: list[Finding]) -> str:
    return json.dumps(
        [{"severity": f.severity.value, "title": f.title, "advice": f.advice} for f in findings],
        ensure_ascii=False,
    )


def _findings_from_json(raw: str) -> list[Finding]:
    return [Finding(Severity(f["severity"]), f["title"], f["advice"]) for f in json.loads(raw)]


# ---------------------------------------------------------------------------
# Repositories
# ---------------------------------------------------------------------------


class SqliteGrowRepository:
    def __init__(self, db: Database) -> None:
        self._db = db

    def active(self) -> Grow | None:
        with self._db.connect() as con:
            row = con.execute(
                "SELECT * FROM grows WHERE archived_on IS NULL ORDER BY id DESC LIMIT 1"
            ).fetchone()
            return self._load(con, row) if row else None

    def get(self, grow_id: int) -> Grow | None:
        with self._db.connect() as con:
            row = con.execute("SELECT * FROM grows WHERE id = ?", (grow_id,)).fetchone()
            return self._load(con, row) if row else None

    def archived(self) -> list[Grow]:
        with self._db.connect() as con:
            rows = con.execute(
                "SELECT * FROM grows WHERE archived_on IS NOT NULL ORDER BY sow_date DESC"
            ).fetchall()
            return [self._load(con, r) for r in rows]

    def add(self, grow: Grow) -> Grow:
        with self._db.connect() as con:
            cur = con.execute(
                "INSERT INTO grows (name, sow_date, plant_count, seed_json) VALUES (?, ?, ?, ?)",
                (grow.name, grow.sow_date.isoformat(), grow.plant_count, _seed_to_json(grow.seed)),
            )
            grow.id = cur.lastrowid
        return grow

    def archive(self, grow_id: int, on: date, summary: str) -> None:
        with self._db.connect() as con:
            con.execute(
                "UPDATE grows SET archived_on = ?, summary = ? WHERE id = ?",
                (on.isoformat(), summary, grow_id),
            )

    def set_milestone(self, grow_id: int, milestone: Milestone) -> None:
        with self._db.connect() as con:
            con.execute(
                """INSERT INTO milestones (grow_id, type, status, reached_on, note, by_ai)
                   VALUES (?, ?, ?, ?, ?, ?)
                   ON CONFLICT (grow_id, type) DO UPDATE SET
                     status = excluded.status, reached_on = excluded.reached_on,
                     note = excluded.note, by_ai = excluded.by_ai""",
                (
                    grow_id,
                    milestone.type.value,
                    milestone.status.value,
                    milestone.reached_on.isoformat(),
                    milestone.note,
                    int(milestone.by_ai),
                ),
            )

    def remove_milestone(self, grow_id: int, kind: MilestoneType) -> None:
        with self._db.connect() as con:
            con.execute("DELETE FROM milestones WHERE grow_id = ? AND type = ?", (grow_id, kind.value))

    @staticmethod
    def _load(con: sqlite3.Connection, row: sqlite3.Row) -> Grow:
        milestones = [
            Milestone(
                MilestoneType(m["type"]),
                MilestoneStatus(m["status"]),
                date.fromisoformat(m["reached_on"]),
                m["note"],
                bool(m["by_ai"]),
            )
            for m in con.execute("SELECT * FROM milestones WHERE grow_id = ?", (row["id"],))
        ]
        milestones.sort(key=lambda m: m.type.order)
        return Grow(
            id=row["id"],
            name=row["name"],
            sow_date=date.fromisoformat(row["sow_date"]),
            plant_count=row["plant_count"],
            seed=_seed_from_json(row["seed_json"]),
            archived_on=date.fromisoformat(row["archived_on"]) if row["archived_on"] else None,
            summary=row["summary"],
            milestones=milestones,
        )


class SqliteSnapshotRepository:
    def __init__(self, db: Database) -> None:
        self._db = db

    def add(self, snapshot: Snapshot) -> Snapshot:
        with self._db.connect() as con:
            cur = con.execute(
                "INSERT INTO snapshots (grow_id, taken_at, path, scheduled) VALUES (?, ?, ?, ?)",
                (snapshot.grow_id, snapshot.taken_at.isoformat(), snapshot.path, int(snapshot.scheduled)),
            )
            snapshot.id = cur.lastrowid
        return snapshot

    def get(self, snapshot_id: int) -> Snapshot | None:
        with self._db.connect() as con:
            row = con.execute("SELECT * FROM snapshots WHERE id = ?", (snapshot_id,)).fetchone()
        return self._from_row(row) if row else None

    def latest(self, grow_id: int) -> Snapshot | None:
        with self._db.connect() as con:
            row = con.execute(
                "SELECT * FROM snapshots WHERE grow_id = ? ORDER BY taken_at DESC LIMIT 1", (grow_id,)
            ).fetchone()
        return self._from_row(row) if row else None

    def scheduled_between(self, grow_id: int, start: datetime, end: datetime) -> list[Snapshot]:
        with self._db.connect() as con:
            rows = con.execute(
                """SELECT * FROM snapshots WHERE grow_id = ? AND scheduled = 1
                   AND taken_at >= ? AND taken_at < ? ORDER BY taken_at""",
                (grow_id, start.isoformat(), end.isoformat()),
            ).fetchall()
        return [self._from_row(r) for r in rows]

    def count(self, grow_id: int) -> int:
        with self._db.connect() as con:
            return con.execute("SELECT COUNT(*) FROM snapshots WHERE grow_id = ?", (grow_id,)).fetchone()[0]

    @staticmethod
    def _from_row(row: sqlite3.Row) -> Snapshot:
        return Snapshot(
            row["id"], row["grow_id"], datetime.fromisoformat(row["taken_at"]), row["path"], bool(row["scheduled"])
        )


class SqliteAnalysisRepository:
    def __init__(self, db: Database) -> None:
        self._db = db

    def add(self, analysis: Analysis) -> Analysis:
        with self._db.connect() as con:
            cur = con.execute(
                """INSERT INTO analyses (grow_id, snapshot_id, created_at, health_score, headline,
                   findings_json, suggested_milestone, milestone_note) VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                (
                    analysis.grow_id,
                    analysis.snapshot_id,
                    analysis.created_at.isoformat(),
                    analysis.health_score,
                    analysis.headline,
                    _findings_to_json(analysis.findings),
                    analysis.suggested_milestone.value if analysis.suggested_milestone else None,
                    analysis.milestone_note,
                ),
            )
            analysis.id = cur.lastrowid
        return analysis

    def latest(self, grow_id: int) -> Analysis | None:
        with self._db.connect() as con:
            row = con.execute(
                "SELECT * FROM analyses WHERE grow_id = ? ORDER BY created_at DESC LIMIT 1", (grow_id,)
            ).fetchone()
        return self._from_row(row) if row else None

    def between(self, grow_id: int, start: datetime, end: datetime) -> list[Analysis]:
        with self._db.connect() as con:
            rows = con.execute(
                """SELECT * FROM analyses WHERE grow_id = ? AND created_at >= ? AND created_at < ?
                   ORDER BY created_at""",
                (grow_id, start.isoformat(), end.isoformat()),
            ).fetchall()
        return [self._from_row(r) for r in rows]

    def average_score(self, grow_id: int) -> float | None:
        with self._db.connect() as con:
            return con.execute("SELECT AVG(health_score) FROM analyses WHERE grow_id = ?", (grow_id,)).fetchone()[0]

    @staticmethod
    def _from_row(row: sqlite3.Row) -> Analysis:
        return Analysis(
            id=row["id"],
            grow_id=row["grow_id"],
            snapshot_id=row["snapshot_id"],
            created_at=datetime.fromisoformat(row["created_at"]),
            health_score=row["health_score"],
            headline=row["headline"],
            findings=_findings_from_json(row["findings_json"]),
            suggested_milestone=MilestoneType(row["suggested_milestone"]) if row["suggested_milestone"] else None,
            milestone_note=row["milestone_note"],
        )


class SqliteDiaryRepository:
    def __init__(self, db: Database) -> None:
        self._db = db

    def add(self, entry: DiaryEntry) -> DiaryEntry:
        with self._db.connect() as con:
            cur = con.execute(
                "INSERT INTO diary (grow_id, created_at, kind, text) VALUES (?, ?, ?, ?)",
                (entry.grow_id, entry.created_at.isoformat(), entry.kind.value, entry.text),
            )
            entry.id = cur.lastrowid
        return entry

    def recent(self, grow_id: int, limit: int = 20) -> list[DiaryEntry]:
        with self._db.connect() as con:
            rows = con.execute(
                "SELECT * FROM diary WHERE grow_id = ? ORDER BY created_at DESC LIMIT ?", (grow_id, limit)
            ).fetchall()
        return [self._from_row(r) for r in rows]

    def between(self, grow_id: int, start: datetime, end: datetime) -> list[DiaryEntry]:
        with self._db.connect() as con:
            rows = con.execute(
                """SELECT * FROM diary WHERE grow_id = ? AND created_at >= ? AND created_at < ?
                   ORDER BY created_at""",
                (grow_id, start.isoformat(), end.isoformat()),
            ).fetchall()
        return [self._from_row(r) for r in rows]

    def delete(self, entry_id: int) -> None:
        with self._db.connect() as con:
            con.execute("DELETE FROM diary WHERE id = ?", (entry_id,))

    @staticmethod
    def _from_row(row: sqlite3.Row) -> DiaryEntry:
        return DiaryEntry(
            row["id"], row["grow_id"], datetime.fromisoformat(row["created_at"]), DiaryKind(row["kind"]), row["text"]
        )


class SqliteReportRepository:
    def __init__(self, db: Database) -> None:
        self._db = db

    def add(self, report: WeeklyReport) -> WeeklyReport:
        body = {
            "happened": report.happened,
            "done": report.done,
            "next_steps": report.next_steps,
            "snapshot_ids": report.snapshot_ids,
        }
        with self._db.connect() as con:
            cur = con.execute(
                """INSERT INTO reports (grow_id, week_start, headline, body_json, needs_attention)
                   VALUES (?, ?, ?, ?, ?)""",
                (
                    report.grow_id,
                    report.week_start.isoformat(),
                    report.headline,
                    json.dumps(body, ensure_ascii=False),
                    int(report.needs_attention),
                ),
            )
            report.id = cur.lastrowid
        return report

    def for_grow(self, grow_id: int) -> list[WeeklyReport]:
        with self._db.connect() as con:
            rows = con.execute(
                "SELECT * FROM reports WHERE grow_id = ? ORDER BY week_start DESC", (grow_id,)
            ).fetchall()
        return [self._from_row(r) for r in rows]

    def get(self, report_id: int) -> WeeklyReport | None:
        with self._db.connect() as con:
            row = con.execute("SELECT * FROM reports WHERE id = ?", (report_id,)).fetchone()
        return self._from_row(row) if row else None

    def exists(self, grow_id: int, week_start: date) -> bool:
        with self._db.connect() as con:
            return (
                con.execute(
                    "SELECT 1 FROM reports WHERE grow_id = ? AND week_start = ?", (grow_id, week_start.isoformat())
                ).fetchone()
                is not None
            )

    @staticmethod
    def _from_row(row: sqlite3.Row) -> WeeklyReport:
        body = json.loads(row["body_json"])
        return WeeklyReport(
            id=row["id"],
            grow_id=row["grow_id"],
            week_start=date.fromisoformat(row["week_start"]),
            headline=row["headline"],
            happened=body["happened"],
            done=body["done"],
            next_steps=body["next_steps"],
            needs_attention=bool(row["needs_attention"]),
            snapshot_ids=body.get("snapshot_ids", []),
        )


class SqliteSeedCache:
    def __init__(self, db: Database) -> None:
        self._db = db

    def get(self, query: str) -> list[SeedInfo] | None:
        with self._db.connect() as con:
            row = con.execute("SELECT seeds_json FROM seed_cache WHERE query = ?", (query,)).fetchone()
        return [seed_from_dict(d) for d in json.loads(row[0])] if row else None

    def put(self, query: str, seeds: list[SeedInfo]) -> None:
        payload = json.dumps([json.loads(_seed_to_json(s)) for s in seeds], ensure_ascii=False)
        with self._db.connect() as con:
            con.execute(
                "INSERT OR REPLACE INTO seed_cache (query, seeds_json, cached_at) VALUES (?, ?, ?)",
                (query, payload, datetime.now().isoformat()),
            )


class SqliteSettingsStore:
    def __init__(self, db: Database) -> None:
        self._db = db

    def get(self, key: str, default: str = "") -> str:
        with self._db.connect() as con:
            row = con.execute("SELECT value FROM settings WHERE key = ?", (key,)).fetchone()
        return row[0] if row else default

    def set(self, key: str, value: str) -> None:
        with self._db.connect() as con:
            con.execute("INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)", (key, value))
