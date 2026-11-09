"""File-system image store and system clock."""

from __future__ import annotations

from datetime import datetime
from pathlib import Path


class FileImageStore:
    """Stores JPEGs as <root>/<grow_id>/<YYYY-MM-DD>/<HHMMSS>.jpg."""

    def __init__(self, root: Path) -> None:
        self._root = root.resolve()
        self._root.mkdir(parents=True, exist_ok=True)

    def save(self, grow_id: int, taken_at: datetime, data: bytes) -> str:
        relative = Path(str(grow_id), taken_at.strftime("%Y-%m-%d"), taken_at.strftime("%H%M%S_%f") + ".jpg")
        target = self._root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        return relative.as_posix()

    def load(self, path: str) -> bytes:
        target = (self._root / path).resolve()
        if self._root not in target.parents:  # never serve files outside the image root
            raise ValueError("invalid image path")
        return target.read_bytes()


class SystemClock:
    def now(self) -> datetime:
        return datetime.now().replace(microsecond=0)
