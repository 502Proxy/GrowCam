"""Reading and triggering the self-updater (see scripts/update.py).

The updater runs outside the web app (as a Windows scheduled task) so it can
restart the app service. The two only share small JSON files in the data dir.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

STATUS_FILE = "update_status.json"
SETTINGS_FILE = "update_settings.json"
APPROVAL_FILE = "update_approved"
TASK_NAME = "GrowCam Updater"


@dataclass(frozen=True)
class UpdateStatus:
    current: str
    latest: str
    state: str  # "up_to_date" | "available" | "installing" | "error" | "unknown"
    checked_at: str
    message: str = ""

    @property
    def update_available(self) -> bool:
        return self.state == "available"


class UpdateManager:
    def __init__(self, data_dir: Path, project_dir: Path, fallback_version: str) -> None:
        self._data = data_dir
        self._project = project_dir
        self._fallback_version = fallback_version

    def status(self) -> UpdateStatus:
        raw = self._read_json(STATUS_FILE)
        return UpdateStatus(
            current=raw.get("current") or self._fallback_version,
            latest=raw.get("latest", ""),
            state=raw.get("state", "unknown"),
            checked_at=raw.get("checked_at", ""),
            message=raw.get("message", ""),
        )

    def auto_install(self) -> bool:
        return bool(self._read_json(SETTINGS_FILE).get("auto_install", True))

    def set_auto_install(self, enabled: bool) -> None:
        settings = self._read_json(SETTINGS_FILE)
        settings["auto_install"] = enabled
        (self._data / SETTINGS_FILE).write_text(json.dumps(settings), encoding="utf-8")

    def check_now(self) -> None:
        self._trigger()

    def install_now(self) -> None:
        (self._data / APPROVAL_FILE).write_text("yes", encoding="utf-8")
        self._trigger()

    def _trigger(self) -> None:
        if os.name == "nt":
            # Runs the scheduled task, i.e. outside the service process tree.
            subprocess.run(
                ["schtasks", "/Run", "/TN", TASK_NAME],
                capture_output=True,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
        else:
            subprocess.Popen(
                [sys.executable, str(self._project / "scripts" / "update.py")],
                cwd=self._project,
                start_new_session=True,
            )

    def _read_json(self, name: str) -> dict:
        try:
            return json.loads((self._data / name).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}
