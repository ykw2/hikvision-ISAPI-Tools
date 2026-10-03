"""攝影機清單。2000 支鏡頭用 CSV 維護，密碼可以留空改走環境變數。"""

from __future__ import annotations

import csv
import io
import re
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path

from hik_isapi.errors import ConfigError
from hik_isapi.values import parse_bool

RESERVED_COLUMNS = {
    "id",
    "host",
    "port",
    "username",
    "password",
    "https",
    "name",
    "enabled",
    "tags",
}


@dataclass(frozen=True)
class Camera:
    id: str
    host: str
    name: str
    port: int | None = None
    username: str | None = None
    password: str | None = field(default=None, repr=False)
    use_https: bool | None = None
    enabled: bool = True
    tags: tuple[str, ...] = ()
    attributes: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class ResolvedCamera:
    """已經補上帳號、密碼、埠與協定，可以直接連線。"""

    id: str
    host: str
    name: str
    port: int
    username: str
    use_https: bool
    tags: tuple[str, ...]
    attributes: dict[str, str]
    password: str = field(repr=False)

    def template_fields(self) -> dict[str, str]:
        fields = dict(self.attributes)
        fields.update(
            {
                "id": self.id,
                "host": self.host,
                "port": str(self.port),
                "name": self.name,
                "username": self.username,
                "https": "true" if self.use_https else "false",
                "tags": ";".join(self.tags),
            }
        )
        return fields


def load_inventory(path: str | Path) -> list[Camera]:
    file = Path(path)
    try:
        text = file.read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise ConfigError(f"無法讀取清單 {file}：{exc}") from exc
    if not text.strip():
        raise ConfigError(f"清單是空的：{file}")

    reader = csv.DictReader(io.StringIO(text))
    if not reader.fieldnames:
        raise ConfigError(f"清單沒有標題列：{file}")
    reader.fieldnames = [name.strip() for name in reader.fieldnames]
    if "id" not in reader.fieldnames or "host" not in reader.fieldnames:
        raise ConfigError("清單必須包含 id 與 host 欄")

    cameras: list[Camera] = []
    seen: set[str] = set()
    for line_no, row in enumerate(reader, start=2):
        normalized = _normalize_row(row)
        if not any(normalized.values()):
            continue
        camera = _parse_row(file, line_no, normalized)
        if camera.id in seen:
            raise ConfigError(f"{file}:{line_no} id 重複：{camera.id}")
        seen.add(camera.id)
        cameras.append(camera)
    if not cameras:
        raise ConfigError(f"清單沒有任何攝影機：{file}")
    return cameras


def select_cameras(
    cameras: Sequence[Camera],
    *,
    tags: Sequence[str] = (),
    only: set[str] | None = None,
    offset: int = 0,
    limit: int | None = None,
) -> list[Camera]:
    """依啟用狀態、id、標籤、offset 與 limit 篩出這次要跑的攝影機。"""
    if offset < 0:
        raise ConfigError("offset 不能是負數")
    if limit is not None and limit < 1:
        raise ConfigError("limit 必須大於 0")

    by_id = {camera.id: camera for camera in cameras}
    if only is not None:
        missing = [camera_id for camera_id in only if camera_id not in by_id]
        if missing:
            preview = "、".join(sorted(missing)[:20])
            raise ConfigError(f"清單沒有這些 id：{preview}")
        disabled = [camera_id for camera_id in only if not by_id[camera_id].enabled]
        if disabled:
            preview = "、".join(sorted(disabled)[:20])
            raise ConfigError(f"這些攝影機已停用：{preview}")

    wanted_tags = {tag for tag in tags if tag}
    chosen: list[Camera] = []
    for camera in cameras:
        if not camera.enabled:
            continue
        if only is not None and camera.id not in only:
            continue
        if wanted_tags and not any(tag in camera.tags for tag in wanted_tags):
            continue
        chosen.append(camera)
    sliced = chosen[offset:]
    if limit is not None:
        sliced = sliced[:limit]
    return sliced


def inventory_warnings(cameras: Sequence[Camera]) -> list[str]:
    seen: dict[tuple[str, int | None, bool | None], str] = {}
    warnings: list[str] = []
    for camera in cameras:
        key = (camera.host, camera.port, camera.use_https)
        previous = seen.get(key)
        if previous is not None:
            warnings.append(f"{camera.id} 與 {previous} 的主機、埠與協定相同")
        else:
            seen[key] = camera.id
    return warnings


def load_failed_ids(path: str | Path) -> set[str]:
    """從先前的 JSON 報告取出失敗或未執行的攝影機 id。"""
    import json

    file = Path(path)
    try:
        data = json.loads(file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ConfigError(f"無法讀取報告 {file}：{exc}") from exc
    rows = data.get("cameras") if isinstance(data, dict) else None
    if not isinstance(rows, list):
        raise ConfigError("報告格式不正確，找不到 cameras")
    failed: set[str] = set()
    for item in rows:
        if isinstance(item, dict) and item.get("id") and not item.get("ok"):
            failed.add(str(item["id"]))
    return failed


def _normalize_row(row: Mapping[str | None, str | None]) -> dict[str, str]:
    normalized: dict[str, str] = {}
    for key, value in row.items():
        if key is None:
            continue
        normalized[key.strip()] = (value or "").strip()
    return normalized


def _parse_row(file: Path, line_no: int, row: Mapping[str, str]) -> Camera:
    where = f"{file}:{line_no}"
    camera_id = row.get("id", "")
    host = row.get("host", "")
    if not camera_id or not host:
        raise ConfigError(f"{where} 需要 id 與 host")
    if any(char.isspace() for char in camera_id):
        raise ConfigError(f"{where} id 不能包含空白：{camera_id}")
    _validate_host(host, where)
    port = _parse_port(row.get("port", ""), where)
    https_text = row.get("https", "")
    use_https = None if https_text == "" else parse_bool(https_text, f"{where} https")
    enabled_text = row.get("enabled", "")
    enabled = True if enabled_text == "" else parse_bool(enabled_text, f"{where} enabled")
    attributes = {key: value for key, value in row.items() if key not in RESERVED_COLUMNS and value != ""}
    return Camera(
        id=camera_id,
        host=host,
        name=row.get("name") or camera_id,
        port=port,
        username=row.get("username") or None,
        password=row.get("password") or None,
        use_https=use_https,
        enabled=enabled,
        tags=_split_tags(row.get("tags", "")),
        attributes=attributes,
    )


def _validate_host(host: str, where: str) -> None:
    if any(char in host for char in " /@"):
        raise ConfigError(f"{where} host 格式不正確：{host}")
    if host.count(":") == 1:
        raise ConfigError(f"{where} 請把埠寫在 port 欄，host 不要包含冒號")
    if ":" in host and not (host.startswith("[") and host.endswith("]")):
        raise ConfigError(f"{where} IPv6 請用方括號，例如 [2001:db8::1]")


def _parse_port(value: str, where: str) -> int | None:
    if value == "":
        return None
    if not value.isdigit():
        raise ConfigError(f"{where} port 必須是 1 到 65535 的整數")
    port = int(value)
    if not 1 <= port <= 65535:
        raise ConfigError(f"{where} port 必須是 1 到 65535 的整數")
    return port


def _split_tags(value: str) -> tuple[str, ...]:
    if not value:
        return ()
    return tuple(part.strip() for part in re.split(r"[;|]", value) if part.strip())


def preview_ids(ids: Iterable[str], limit: int = 20) -> str:
    items = list(ids)
    shown = "、".join(items[:limit])
    if len(items) > limit:
        return f"{shown} 等 {len(items)} 支"
    return shown
