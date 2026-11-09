"""Self-updater for GrowCam (stdlib only).

Run periodically (Windows scheduled task "GrowCam Updater", every 15 minutes).
Only release tags (v1.2.3) are deployed, never plain pushes to main:

    git tag v0.2.0 && git push origin v0.2.0

Steps: fetch tags -> compare with the deployed tag -> if newer and allowed,
check out the tag, install requirements and restart the service. On failure
the previous version is restored.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from datetime import datetime
from pathlib import Path

PROJECT = Path(__file__).resolve().parent.parent
DATA = Path(os.environ.get("GROWCAM_DATA_DIR", PROJECT / "data"))
STATUS_FILE = DATA / "update_status.json"
SETTINGS_FILE = DATA / "update_settings.json"
APPROVAL_FILE = DATA / "update_approved"
SERVICE_NAME = "GrowCam"


def run(*cmd: str) -> str:
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    result = subprocess.run(cmd, cwd=PROJECT, capture_output=True, text=True, creationflags=flags)
    if result.returncode != 0:
        raise RuntimeError(f"{' '.join(cmd)}: {result.stderr.strip() or result.stdout.strip()}")
    return result.stdout.strip()


def version_key(tag: str) -> tuple[int, ...]:
    try:
        return tuple(int(part) for part in tag.lstrip("v").split("."))
    except ValueError:
        return (-1,)


def latest_tag() -> str:
    tags = [t for t in run("git", "tag", "--list", "v*").splitlines() if version_key(t) != (-1,)]
    return max(tags, key=version_key) if tags else ""


def current_version() -> str:
    try:
        return run("git", "describe", "--tags", "--exact-match")
    except RuntimeError:
        return run("git", "rev-parse", "--short", "HEAD")


def write_status(**fields: str) -> None:
    DATA.mkdir(parents=True, exist_ok=True)
    fields["checked_at"] = datetime.now().strftime("%d.%m.%Y %H:%M")
    STATUS_FILE.write_text(json.dumps(fields, ensure_ascii=False), encoding="utf-8")


def auto_install_enabled() -> bool:
    try:
        return bool(json.loads(SETTINGS_FILE.read_text(encoding="utf-8")).get("auto_install", True))
    except (OSError, ValueError):
        return True


def is_newer(latest: str, current: str) -> bool:
    if not latest:
        return False
    if version_key(current) == (-1,):  # running an untagged commit: any release is "newer"
        return True
    return version_key(latest) > version_key(current)


def install(tag: str, previous: str) -> None:
    run("git", "checkout", "--force", tag)
    try:
        run(sys.executable, "-m", "pip", "install", "--disable-pip-version-check", "-q", "-r", "requirements.txt")
    except RuntimeError:
        run("git", "checkout", "--force", previous)  # roll back the code, keep the old deps
        raise
    restart_service()


def restart_service() -> None:
    if os.name == "nt":
        run("nssm", "restart", SERVICE_NAME)


def main() -> int:
    current = current_version()
    try:
        run("git", "fetch", "--tags", "--prune", "--force", "origin")
        latest = latest_tag()
        if not is_newer(latest, current):
            write_status(current=current, latest=latest or current, state="up_to_date")
            return 0

        approved = APPROVAL_FILE.exists()
        if not (auto_install_enabled() or approved):
            write_status(current=current, latest=latest, state="available")
            return 0

        write_status(current=current, latest=latest, state="installing")
        APPROVAL_FILE.unlink(missing_ok=True)
        previous = run("git", "rev-parse", "HEAD")
        install(latest, previous)
        write_status(current=latest, latest=latest, state="up_to_date")
        return 0
    except Exception as exc:  # report everything to the UI, never crash silently
        write_status(current=current, latest="", state="error", message=str(exc)[:300])
        return 1


if __name__ == "__main__":
    sys.exit(main())
