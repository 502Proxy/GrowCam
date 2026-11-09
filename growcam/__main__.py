"""Entry point: `python -m growcam`."""

from __future__ import annotations

import logging
from logging.handlers import RotatingFileHandler

import uvicorn

from growcam.config import Settings
from growcam.container import build_container
from growcam.web.app import create_app


def main() -> None:
    settings = Settings.from_env()
    settings.data_dir.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
        handlers=[
            logging.StreamHandler(),
            RotatingFileHandler(settings.data_dir / "growcam.log", maxBytes=1_000_000, backupCount=3, encoding="utf-8"),
        ],
    )
    app = create_app(build_container(settings))
    uvicorn.run(app, host=settings.host, port=settings.port, log_level="warning")


if __name__ == "__main__":
    main()
