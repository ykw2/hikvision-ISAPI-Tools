"""執行設定。密碼與 token 只從環境變數或 data volume 來，不寫進程式。"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    data_dir: Path
    database_path: Path
    hik_basic_user: str = ""
    hik_basic_password: str = ""
    cf_ingest_url: str = ""
    cf_ingest_token: str = ""
    max_image_bytes: int = 1_048_576
    max_attempts: int = 8
    run_forwarder: bool = True

    @property
    def password_path(self) -> Path:
        return self.data_dir / "gui_password.hash"

    @property
    def session_secret_path(self) -> Path:
        return self.data_dir / "session_secret"

    @classmethod
    def from_env(cls) -> Settings:
        data_dir = Path(os.environ.get("DATA_DIR", "data"))
        database_path = Path(os.environ.get("DATABASE_PATH", str(data_dir / "events.db")))
        raw_limit = os.environ.get("MAX_IMAGE_BYTES", "1048576")
        try:
            max_image_bytes = int(raw_limit)
        except ValueError:
            max_image_bytes = 1_048_576
        if max_image_bytes < 0:
            max_image_bytes = 0
        return cls(
            data_dir=data_dir,
            database_path=database_path,
            hik_basic_user=os.environ.get("HIK_BASIC_USER", ""),
            hik_basic_password=os.environ.get("HIK_BASIC_PASSWORD", ""),
            cf_ingest_url=os.environ.get("CF_INGEST_URL", "").strip(),
            cf_ingest_token=os.environ.get("CF_INGEST_TOKEN", ""),
            max_image_bytes=max_image_bytes,
        )
