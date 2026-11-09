"""Background jobs: hourly photos, the daily analysis and the Sunday report.

A plain asyncio loop instead of a scheduler library – three jobs don't need
more. Last-run markers are persisted so a restart never runs a job twice.
"""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timedelta

from growcam.application.services import NoActiveGrowError
from growcam.container import Container

log = logging.getLogger("growcam.scheduler")

TICK_SECONDS = 30
SUNDAY = 6


class Scheduler:
    def __init__(self, container: Container) -> None:
        self._c = container
        self._task: asyncio.Task | None = None

    def start(self) -> None:
        self._task = asyncio.create_task(self._run(), name="growcam-scheduler")

    async def stop(self) -> None:
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass

    async def _run(self) -> None:
        while True:
            try:
                await self.tick(self._c.clock.now())
            except Exception:  # keep the loop alive whatever happens
                log.exception("scheduler tick failed")
            await asyncio.sleep(TICK_SECONDS)

    async def tick(self, now: datetime) -> None:
        if self._c.grows.active() is None:
            return
        await self._daily_analysis(now)
        await self._periodic_snapshot(now)
        await self._weekly_report(now)

    async def _daily_analysis(self, now: datetime) -> None:
        settings = self._c.settings
        if now.time() < settings.analysis_time or self._marker("last_analysis") == now.date().isoformat():
            return
        self._set_marker("last_analysis", now.date().isoformat())
        await self._job("daily analysis", self._c.monitoring.analyze_now)

    async def _periodic_snapshot(self, now: datetime) -> None:
        settings = self._c.settings
        interval = settings.snapshot_interval_minutes
        if interval <= 0 or not settings.is_light_on(now.time()):
            return
        last = self._marker("last_snapshot")
        if last and now - datetime.fromisoformat(last) < timedelta(minutes=interval):
            return
        self._set_marker("last_snapshot", now.isoformat())
        await self._job("snapshot", self._c.monitoring.take_snapshot, True)

    async def _weekly_report(self, now: datetime) -> None:
        if now.weekday() != SUNDAY or now.time() < self._c.settings.report_time:
            return
        if self._marker("last_report") == now.date().isoformat():
            return
        self._set_marker("last_report", now.date().isoformat())
        await self._job("weekly report", self._c.reports.generate_last_week_if_missing)

    async def _job(self, name: str, func, *args) -> None:
        log.info("running %s", name)
        try:
            await asyncio.to_thread(func, *args)
        except NoActiveGrowError:
            pass
        except Exception:
            log.exception("%s failed", name)

    def _marker(self, key: str) -> str:
        return self._c.state.get(f"scheduler.{key}")

    def _set_marker(self, key: str, value: str) -> None:
        self._c.state.set(f"scheduler.{key}", value)
