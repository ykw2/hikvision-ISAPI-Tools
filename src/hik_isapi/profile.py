"""宣告式設定檔。新增一種設定時，通常只加一個 step，不必改程式。"""

from __future__ import annotations

import re
from dataclasses import dataclass, field, replace
from pathlib import Path

import yaml

from hik_isapi.errors import ConfigError
from hik_isapi.values import as_float, as_int, parse_bool, xml_text
from hik_isapi.version import __version__

_STEP_ID = re.compile(r"[A-Za-z_][\w\-]*")
_MODES = {"request", "merge_xml", "assert_xml"}
_METHODS = {"GET", "PUT", "POST", "DELETE", "PATCH", "HEAD"}
_ON_ERROR = {"abort_camera", "continue"}
_ON_MISSING = {"error", "skip", "create"}
_PROFILE_FIELDS = {
    "name",
    "description",
    "concurrency",
    "timeout_seconds",
    "retries",
    "retry_backoff_seconds",
    "username",
    "password_env",
    "use_https",
    "verify_tls",
    "vars",
    "max_failure_ratio",
    "safety_min_samples",
    "user_agent",
    "steps",
}
_STEP_FIELDS = {
    "id",
    "description",
    "mode",
    "method",
    "path",
    "on_error",
    "on_missing",
    "content_type",
    "body",
    "body_file",
    "set",
    "expect",
    "remove",
    "headers",
    "force",
    "capture_body",
}


def default_user_agent() -> str:
    return f"hik-isapi/{__version__}"


@dataclass(frozen=True)
class Step:
    id: str
    path: str
    mode: str = "request"
    method: str = "GET"
    description: str = ""
    on_error: str = "abort_camera"
    on_missing: str = "error"
    content_type: str = "application/xml"
    body: str | None = None
    set_values: tuple[tuple[str, str], ...] = ()
    expect_values: tuple[tuple[str, str], ...] = ()
    remove_paths: tuple[str, ...] = ()
    headers: tuple[tuple[str, str], ...] = ()
    force: bool = False
    capture_body: bool = False


@dataclass(frozen=True)
class Profile:
    name: str
    steps: tuple[Step, ...]
    description: str = ""
    concurrency: int = 20
    timeout_seconds: float = 20.0
    retries: int = 2
    retry_backoff_seconds: float = 0.5
    username: str = "admin"
    password_env: str = "HIK_PASSWORD"
    use_https: bool = False
    verify_tls: bool = False
    vars: dict[str, str] = field(default_factory=dict)
    max_failure_ratio: float | None = 0.2
    safety_min_samples: int = 20
    user_agent: str = field(default_factory=default_user_agent)

    def validate(self) -> None:
        if not self.name.strip():
            raise ConfigError("profile 缺少 name")
        if not self.steps:
            raise ConfigError("profile 至少需要一個 step")
        ids = [step.id for step in self.steps]
        if len(ids) != len(set(ids)):
            raise ConfigError("step id 重複")
        if not 1 <= self.concurrency <= 256:
            raise ConfigError("concurrency 必須介於 1 到 256")
        if not 1 <= self.timeout_seconds <= 180:
            raise ConfigError("timeout_seconds 必須介於 1 到 180")
        if not 0 <= self.retries <= 5:
            raise ConfigError("retries 必須介於 0 到 5")
        if self.retry_backoff_seconds < 0:
            raise ConfigError("retry_backoff_seconds 不能是負數")
        if self.max_failure_ratio is not None and not 0 <= self.max_failure_ratio <= 1:
            raise ConfigError("max_failure_ratio 必須介於 0 到 1")
        if self.safety_min_samples < 1:
            raise ConfigError("safety_min_samples 必須大於 0")
        if not self.username.strip():
            raise ConfigError("username 不能是空的")
        if not self.password_env.strip():
            raise ConfigError("password_env 不能是空的")
        for step in self.steps:
            _validate_step(step)

    def with_overrides(self, **kwargs: object) -> Profile:
        clean = {key: value for key, value in kwargs.items() if value is not None}
        updated = replace(self, **clean)  # type: ignore[arg-type]
        updated.validate()
        return updated


def load_profile(path: str | Path) -> Profile:
    file = Path(path)
    try:
        text = file.read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise ConfigError(f"無法讀取設定檔 {file}：{exc}") from exc
    try:
        raw = yaml.safe_load(text)
    except yaml.YAMLError as exc:
        raise ConfigError(f"設定檔 YAML 格式錯誤：{exc}") from exc
    if not isinstance(raw, dict):
        raise ConfigError("設定檔最外層必須是鍵值對應")
    unknown = set(raw) - _PROFILE_FIELDS
    if unknown:
        raise ConfigError("設定檔有未知欄位：" + "、".join(sorted(str(item) for item in unknown)))

    steps_raw = raw.get("steps")
    if not isinstance(steps_raw, list) or not steps_raw:
        raise ConfigError("steps 必須是非空清單")
    steps = tuple(_parse_step(item, file, index) for index, item in enumerate(steps_raw, start=1))
    ratio = _optional_ratio(raw)
    profile = Profile(
        name=str(raw.get("name") or file.stem),
        description=str(raw.get("description") or ""),
        steps=steps,
        concurrency=as_int(raw["concurrency"], "concurrency") if "concurrency" in raw else 20,
        timeout_seconds=as_float(raw["timeout_seconds"], "timeout_seconds") if "timeout_seconds" in raw else 20.0,
        retries=as_int(raw["retries"], "retries") if "retries" in raw else 2,
        retry_backoff_seconds=(
            as_float(raw["retry_backoff_seconds"], "retry_backoff_seconds")
            if "retry_backoff_seconds" in raw
            else 0.5
        ),
        username=str(raw.get("username") or "admin"),
        password_env=str(raw.get("password_env") or "HIK_PASSWORD"),
        use_https=parse_bool(raw["use_https"], "use_https") if "use_https" in raw else False,
        verify_tls=parse_bool(raw["verify_tls"], "verify_tls") if "verify_tls" in raw else False,
        vars=_parse_vars(raw.get("vars")),
        max_failure_ratio=ratio,
        safety_min_samples=as_int(raw["safety_min_samples"], "safety_min_samples") if "safety_min_samples" in raw else 20,
        user_agent=str(raw.get("user_agent") or default_user_agent()),
    )
    profile.validate()
    return profile


def _optional_ratio(raw: dict[str, object]) -> float | None:
    if "max_failure_ratio" not in raw or raw["max_failure_ratio"] is None:
        if "max_failure_ratio" not in raw:
            return 0.2
        return None
    return as_float(raw["max_failure_ratio"], "max_failure_ratio")


def _parse_vars(value: object) -> dict[str, str]:
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise ConfigError("vars 必須是對應")
    return {str(key): xml_text(item) for key, item in value.items()}


def _parse_step(item: object, profile_file: Path, index: int) -> Step:
    if not isinstance(item, dict):
        raise ConfigError(f"第 {index} 個 step 必須是鍵值對應")
    step_id = str(item.get("id") or "").strip()
    if not _STEP_ID.fullmatch(step_id):
        raise ConfigError(f"第 {index} 個 step 的 id 不合法：{step_id or '空白'}")
    unknown = set(item) - _STEP_FIELDS
    if unknown:
        raise ConfigError(f"step {step_id} 有未知欄位：" + "、".join(sorted(str(name) for name in unknown)))

    mode = str(item.get("mode") or "request").strip()
    if mode not in _MODES:
        raise ConfigError(f"step {step_id} 的 mode 必須是 request、merge_xml 或 assert_xml")
    default_method = "PUT" if mode == "merge_xml" else "GET"
    method = str(item.get("method") or default_method).strip().upper()
    path = str(item.get("path") or "").strip()
    if not path.startswith("/"):
        raise ConfigError(f"step {step_id} 的 path 必須以 / 開頭")

    set_values = _parse_pairs(item.get("set"), step_id, "set")
    expect_values = _parse_pairs(item.get("expect"), step_id, "expect")
    body = _load_body(item, profile_file, step_id)
    remove_paths = _parse_remove(item.get("remove"), step_id)
    headers = _parse_headers(item.get("headers"), step_id)

    if mode == "merge_xml" and method != "PUT":
        raise ConfigError(f"step {step_id} 是 merge_xml，method 必須是 PUT")
    if mode == "assert_xml" and method != "GET":
        raise ConfigError(f"step {step_id} 是 assert_xml，method 必須是 GET")
    if mode == "merge_xml" and not set_values and not remove_paths:
        raise ConfigError(f"step {step_id} 是 merge_xml，必須提供 set 或 remove")
    if mode == "assert_xml" and not expect_values:
        raise ConfigError(f"step {step_id} 是 assert_xml，必須提供 expect")
    if mode == "request" and (set_values or expect_values or remove_paths):
        raise ConfigError(f"step {step_id} 是 request，欄位比對請改用 merge_xml 或 assert_xml")
    if mode != "request" and body is not None:
        raise ConfigError(f"step {step_id} 的 body 只用於 request")
    if remove_paths and mode != "merge_xml":
        raise ConfigError(f"step {step_id} 的 remove 只用於 merge_xml")

    return Step(
        id=step_id,
        path=path,
        mode=mode,
        method=method,
        description=str(item.get("description") or ""),
        on_error=str(item.get("on_error") or "abort_camera"),
        on_missing=str(item.get("on_missing") or "error"),
        content_type=str(item.get("content_type") or "application/xml"),
        body=body,
        set_values=set_values,
        expect_values=expect_values,
        remove_paths=remove_paths,
        headers=headers,
        force=parse_bool(item["force"], f"step {step_id} force") if "force" in item else False,
        capture_body=(
            parse_bool(item["capture_body"], f"step {step_id} capture_body") if "capture_body" in item else False
        ),
    )


def _validate_step(step: Step) -> None:
    if step.mode not in _MODES:
        raise ConfigError(f"step {step.id} 的 mode 不正確")
    if step.method not in _METHODS:
        raise ConfigError(f"step {step.id} 的 method 不正確")
    if step.on_error not in _ON_ERROR:
        raise ConfigError(f"step {step.id} 的 on_error 必須是 abort_camera 或 continue")
    if step.on_missing not in _ON_MISSING:
        raise ConfigError(f"step {step.id} 的 on_missing 必須是 error、skip 或 create")
    if not step.path.startswith("/") and "{{" not in step.path:
        raise ConfigError(f"step {step.id} 的 path 必須以 / 開頭")


def _parse_pairs(value: object, step_id: str, field_name: str) -> tuple[tuple[str, str], ...]:
    if value is None:
        return ()
    if not isinstance(value, dict) or not value:
        raise ConfigError(f"step {step_id} 的 {field_name} 必須是非空對應")
    pairs: list[tuple[str, str]] = []
    for key, item in value.items():
        path = str(key).strip()
        if not path:
            raise ConfigError(f"step {step_id} 的 {field_name} 有空白路徑")
        try:
            text = xml_text(item)
        except ConfigError as exc:
            raise ConfigError(f"step {step_id} 的 {field_name}.{path}：{exc}") from exc
        pairs.append((path, text))
    return tuple(pairs)


def _load_body(item: dict[str, object], profile_file: Path, step_id: str) -> str | None:
    body = item.get("body")
    body_file = item.get("body_file")
    if body is not None and body_file is not None:
        raise ConfigError(f"step {step_id} 不能同時寫 body 與 body_file")
    if body is not None:
        if not isinstance(body, str):
            raise ConfigError(f"step {step_id} 的 body 必須是文字")
        return body
    if body_file is None:
        return None
    relative = Path(str(body_file))
    file = relative if relative.is_absolute() else profile_file.parent / relative
    if not file.is_file():
        raise ConfigError(f"step {step_id} 找不到 body_file：{file}")
    try:
        return file.read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise ConfigError(f"step {step_id} 無法讀取 body_file：{exc}") from exc


def _parse_remove(value: object, step_id: str) -> tuple[str, ...]:
    if value is None:
        return ()
    if not isinstance(value, list):
        raise ConfigError(f"step {step_id} 的 remove 必須是清單")
    paths: list[str] = []
    for item in value:
        path = str(item).strip()
        if not path:
            raise ConfigError(f"step {step_id} 的 remove 有空白路徑")
        paths.append(path)
    return tuple(paths)


def _parse_headers(value: object, step_id: str) -> tuple[tuple[str, str], ...]:
    if value is None:
        return ()
    if not isinstance(value, dict):
        raise ConfigError(f"step {step_id} 的 headers 必須是對應")
    return tuple((str(key), xml_text(item)) for key, item in value.items())
