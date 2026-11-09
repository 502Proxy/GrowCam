"""Camera adapters: TP-Link Tapo (C200/C210) and an offline demo camera."""

from __future__ import annotations

import os
import subprocess
import threading
from importlib import resources
from urllib.parse import quote

from growcam.application.ports import CameraError, CameraPreset, Direction

# Degrees per arrow click. Small steps are easier to aim with.
PAN_STEP = 10
TILT_STEP = 5

_MOVES = {
    Direction.LEFT: (-PAN_STEP, 0),
    Direction.RIGHT: (PAN_STEP, 0),
    Direction.UP: (0, TILT_STEP),
    Direction.DOWN: (0, -TILT_STEP),
}


class FfmpegSnapshotter:
    """Grabs a single JPEG frame from an RTSP stream with ffmpeg."""

    def __init__(self, rtsp_url: str, ffmpeg: str = "ffmpeg", timeout: float = 20.0) -> None:
        self._url = rtsp_url
        self._ffmpeg = ffmpeg
        self._timeout = timeout

    def capture(self) -> bytes:
        cmd = [
            self._ffmpeg, "-hide_banner", "-loglevel", "error",
            "-rtsp_transport", "tcp", "-i", self._url,
            "-frames:v", "1", "-q:v", "2", "-f", "image2", "-",
        ]
        # Don't flash a console window when running as a Windows service.
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        try:
            result = subprocess.run(cmd, capture_output=True, timeout=self._timeout, creationflags=flags)
        except FileNotFoundError as exc:
            raise CameraError("ffmpeg wurde nicht gefunden. Ist es installiert?") from exc
        except subprocess.TimeoutExpired as exc:
            raise CameraError("Die Kamera antwortet nicht.") from exc
        if result.returncode != 0 or not result.stdout:
            raise CameraError("Kein Bild von der Kamera: " + result.stderr.decode(errors="ignore").strip()[:200])
        return result.stdout


class TapoCamera:
    """Tapo C200/C210: snapshots via RTSP, pan/tilt and presets via pytapo.

    pytapo is optional – without it (or without the cloud password) the camera
    still delivers photos, only the motor controls are unavailable.
    """

    def __init__(
        self,
        host: str,
        rtsp_user: str,
        rtsp_password: str,
        cloud_password: str = "",
        ffmpeg: str = "ffmpeg",
    ) -> None:
        url = f"rtsp://{quote(rtsp_user, safe='')}:{quote(rtsp_password, safe='')}@{host}:554/stream1"
        self._snapshotter = FfmpegSnapshotter(url, ffmpeg)
        self._host = host
        self._cloud_password = cloud_password
        self._client = None
        self._client_lock = threading.Lock()

    def capture(self) -> bytes:
        return self._snapshotter.capture()

    def move(self, direction: str) -> None:
        dx, dy = _MOVES[direction]
        self._call(lambda c: c.moveMotor(dx, dy))

    def presets(self) -> list[CameraPreset]:
        raw = self._call(lambda c: c.getPresets()) or {}
        return [CameraPreset(str(pid), str(name)) for pid, name in raw.items()]

    def goto_preset(self, preset_id: str) -> None:
        self._call(lambda c: c.setPreset(preset_id))

    def save_preset(self, name: str) -> CameraPreset:
        existing = next((p for p in self.presets() if p.name == name), None)
        if existing is not None:  # replace instead of piling up duplicates
            self._call(lambda c: c.deletePreset(existing.id))
        self._call(lambda c: c.savePreset(name))
        created = next((p for p in self.presets() if p.name == name), None)
        if created is None:
            raise CameraError("Die Position konnte nicht gespeichert werden.")
        return created

    def _call(self, action):
        with self._client_lock:
            try:
                if self._client is None:
                    self._client = self._connect()
                return action(self._client)
            except CameraError:
                raise
            except Exception as exc:  # pytapo raises plain Exceptions
                self._client = None  # reconnect next time (session may have expired)
                raise CameraError(f"Kamera-Steuerung fehlgeschlagen: {exc}") from exc

    def _connect(self):
        if not self._cloud_password:
            raise CameraError("Für die Steuerung fehlt TAPO_CLOUD_PASSWORD in der .env-Datei.")
        try:
            from pytapo import Tapo
        except ImportError as exc:
            raise CameraError("Für die Steuerung muss 'pytapo' installiert sein.") from exc
        # pytapo uses the user "admin" together with the TP-Link cloud password.
        return Tapo(self._host, "admin", self._cloud_password)


class DemoCamera:
    """Offline stand-in so the app can be tried without hardware."""

    def __init__(self) -> None:
        self._image = resources.files("growcam.infrastructure").joinpath("demo.jpg").read_bytes()
        self._presets = [CameraPreset("1", "Alle Pflanzen"), CameraPreset("2", "Pflanze links")]

    def capture(self) -> bytes:
        return self._image

    def move(self, direction: str) -> None:
        if direction not in _MOVES:
            raise CameraError("Unbekannte Richtung")

    def presets(self) -> list[CameraPreset]:
        return list(self._presets)

    def goto_preset(self, preset_id: str) -> None:
        if not any(p.id == preset_id for p in self._presets):
            raise CameraError("Unbekannte Position")

    def save_preset(self, name: str) -> CameraPreset:
        self._presets = [p for p in self._presets if p.name != name]
        preset = CameraPreset(str(max((int(p.id) for p in self._presets), default=0) + 1), name)
        self._presets.append(preset)
        return preset
