"""Configuration from environment variables and an optional .env file."""

from __future__ import annotations

import os
from dataclasses import dataclass
from datetime import time
from pathlib import Path

PROJECT_DIR = Path(__file__).resolve().parent.parent


def load_dotenv(path: Path) -> None:
    """Minimal .env reader: KEY=value lines, '#' comments. Real env vars win."""
    if not path.is_file():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        os.environ.setdefault(key.strip(), value.strip().strip('"').strip("'"))


def _time(value: str) -> time:
    hours, minutes = value.split(":")
    return time(int(hours), int(minutes))


@dataclass(frozen=True)
class Settings:
    data_dir: Path
    host: str
    port: int
    # camera
    tapo_host: str
    tapo_rtsp_user: str
    tapo_rtsp_password: str
    tapo_cloud_password: str
    ffmpeg: str
    # AI
    anthropic_api_key: str
    anthropic_model: str
    anthropic_version: str
    # schedule
    analysis_time: time
    report_time: time
    snapshot_interval_minutes: int
    light_on: time
    light_off: time

    @property
    def demo_camera(self) -> bool:
        return not self.tapo_host

    @property
    def demo_ai(self) -> bool:
        return not self.anthropic_api_key

    def is_light_on(self, now: time) -> bool:
        if self.light_on <= self.light_off:
            return self.light_on <= now < self.light_off
        return now >= self.light_on or now < self.light_off  # schedule crosses midnight

    @classmethod
    def from_env(cls) -> "Settings":
        load_dotenv(PROJECT_DIR / ".env")
        env = os.environ.get
        data_dir = Path(env("GROWCAM_DATA_DIR", str(PROJECT_DIR / "data")))
        return cls(
            data_dir=data_dir,
            host=env("GROWCAM_HOST", "0.0.0.0"),
            port=int(env("GROWCAM_PORT", "8080")),
            tapo_host=env("TAPO_HOST", ""),
            tapo_rtsp_user=env("TAPO_RTSP_USER", ""),
            tapo_rtsp_password=env("TAPO_RTSP_PASSWORD", ""),
            tapo_cloud_password=env("TAPO_CLOUD_PASSWORD", ""),
            ffmpeg=env("FFMPEG_PATH", "ffmpeg"),
            anthropic_api_key=env("ANTHROPIC_API_KEY", ""),
            anthropic_model=env("ANTHROPIC_MODEL", "claude-sonnet-5-5"),
            anthropic_version=env("ANTHROPIC_VERSION", "2023-06-01"),
            analysis_time=_time(env("ANALYSIS_TIME", "12:00")),
            report_time=_time(env("REPORT_TIME", "20:00")),
            snapshot_interval_minutes=int(env("SNAPSHOT_INTERVAL_MINUTES", "60")),
            light_on=_time(env("LIGHT_ON", "06:00")),
            light_off=_time(env("LIGHT_OFF", "00:00")),
        )
