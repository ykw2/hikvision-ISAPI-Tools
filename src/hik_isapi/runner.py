"""把同一份設定套到多支攝影機：逐支依序執行步驟，支與支之間並發。"""

from __future__ import annotations

import csv
import json
import os
import threading
import time
from collections.abc import Callable, Mapping, Sequence
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Protocol
from xml.etree import ElementTree as ET

from hik_isapi.client import IsapiClient, IsapiResponse
from hik_isapi.errors import ConfigError, TemplateError, TransportError, XmlPathError
from hik_isapi.inventory import Camera, ResolvedCamera, preview_ids
from hik_isapi.profile import Profile, Step
from hik_isapi.templating import render
from hik_isapi.xmlutil import edit_xml, find_node, parse_xml, serialize

_MUTATING = {"PUT", "POST", "DELETE", "PATCH"}
ProgressCallback = Callable[["CameraResult", int, int], None]
SafetyCallback = Callable[[str], None]
Sleep = Callable[[float], None]


class SupportsIsapi(Protocol):
    """client_factory 回傳的物件需要這些方法。正式連線使用 IsapiClient。"""

    def request(
        self,
        method: str,
        path: str,
        content: bytes | None = None,
        headers: dict[str, str] | None = None,
    ) -> IsapiResponse: ...

    def close(self) -> None: ...


ClientFactory = Callable[[ResolvedCamera, Profile], SupportsIsapi]


@dataclass
class StepResult:
    id: str
    ok: bool
    skipped: bool = False
    sent: bool = False
    dry_run: bool = False
    method: str | None = None
    path: str | None = None
    http_status: int | None = None
    isapi_status_code: int | None = None
    reboot_required: bool = False
    attempts: int = 0
    elapsed_ms: float = 0
    error: str | None = None
    note: str | None = None
    warnings: list[str] = field(default_factory=list)
    changes: list[dict[str, str | None]] = field(default_factory=list)
    body: str | None = None

    def to_dict(self) -> dict[str, object]:
        data: dict[str, object] = {
            "id": self.id,
            "ok": self.ok,
            "skipped": self.skipped,
            "sent": self.sent,
            "method": self.method,
            "path": self.path,
            "attempts": self.attempts,
            "elapsed_ms": round(self.elapsed_ms, 1),
        }
        if self.dry_run:
            data["dry_run"] = True
        if self.http_status is not None:
            data["http_status"] = self.http_status
        if self.isapi_status_code is not None:
            data["isapi_status_code"] = self.isapi_status_code
        if self.reboot_required:
            data["reboot_required"] = True
        if self.error:
            data["error"] = self.error
        if self.note:
            data["note"] = self.note
        if self.warnings:
            data["warnings"] = list(self.warnings)
        if self.changes:
            data["changes"] = self.changes
        if self.body is not None:
            data["body"] = self.body
        return data


@dataclass
class CameraResult:
    id: str
    host: str
    ok: bool
    skipped: bool
    elapsed_ms: float
    error: str | None
    failed_step: str | None
    steps: list[StepResult]
    note: str | None = None

    @property
    def reboot_required(self) -> bool:
        return any(step.reboot_required for step in self.steps)

    def to_dict(self) -> dict[str, object]:
        data: dict[str, object] = {
            "id": self.id,
            "host": self.host,
            "ok": self.ok,
            "skipped": self.skipped,
            "elapsed_ms": round(self.elapsed_ms, 1),
            "reboot_required": self.reboot_required,
            "steps": [step.to_dict() for step in self.steps],
        }
        if self.failed_step:
            data["failed_step"] = self.failed_step
        if self.error:
            data["error"] = self.error
        if self.note:
            data["note"] = self.note
        return data


@dataclass
class RunReport:
    profile: str
    dry_run: bool
    started_at: str
    finished_at: str
    safety_tripped: bool
    safety_message: str | None
    cameras: list[CameraResult]

    @property
    def success_count(self) -> int:
        return sum(1 for camera in self.cameras if camera.ok)

    @property
    def failed_count(self) -> int:
        return sum(1 for camera in self.cameras if not camera.ok and not camera.skipped)

    @property
    def skipped_count(self) -> int:
        return sum(1 for camera in self.cameras if camera.skipped)

    @property
    def exit_code(self) -> int:
        if self.failed_count or self.skipped_count or self.safety_tripped:
            return 1
        return 0

    def to_dict(self) -> dict[str, object]:
        return {
            "profile": self.profile,
            "dry_run": self.dry_run,
            "started_at": self.started_at,
            "finished_at": self.finished_at,
            "safety_tripped": self.safety_tripped,
            "safety_message": self.safety_message,
            "success_count": self.success_count,
            "failed_count": self.failed_count,
            "skipped_count": self.skipped_count,
            "cameras": [camera.to_dict() for camera in self.cameras],
        }

    def write_json(self, path: str | Path) -> Path:
        file = Path(path)
        if file.suffix.lower() != ".json":
            file = file.with_suffix(".json")
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(json.dumps(self.to_dict(), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        return file

    def write_csv(self, path: str | Path) -> Path:
        file = Path(path)
        file.parent.mkdir(parents=True, exist_ok=True)
        with file.open("w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.DictWriter(
                handle,
                fieldnames=["id", "host", "ok", "skipped", "failed_step", "error", "elapsed_ms", "reboot_required"],
            )
            writer.writeheader()
            for camera in self.cameras:
                writer.writerow(
                    {
                        "id": camera.id,
                        "host": camera.host,
                        "ok": "true" if camera.ok else "false",
                        "skipped": "true" if camera.skipped else "false",
                        "failed_step": camera.failed_step or "",
                        "error": camera.error or "",
                        "elapsed_ms": round(camera.elapsed_ms, 1),
                        "reboot_required": "true" if camera.reboot_required else "false",
                    }
                )
        return file

    def write(self, path: str | Path) -> tuple[Path, Path]:
        json_path = self.write_json(path)
        return json_path, self.write_csv(json_path.with_suffix(".csv"))


@dataclass
class _RunState:
    lock: threading.Lock = field(default_factory=threading.Lock)
    stop: bool = False
    finished: int = 0
    executed: int = 0
    failed: int = 0
    safety_tripped: bool = False
    safety_message: str | None = None


class Runner:
    def __init__(
        self,
        profile: Profile,
        *,
        client_factory: ClientFactory | None = None,
        sleep: Sleep | None = None,
        environ: Mapping[str, str] | None = None,
    ) -> None:
        self.profile = profile
        self._client_factory = client_factory or default_client_factory
        self._sleep = sleep or time.sleep
        self._environ = environ if environ is not None else os.environ

    def run(
        self,
        cameras: Sequence[Camera],
        *,
        dry_run: bool = False,
        password: str | None = None,
        password_env: str | None = None,
        username: str | None = None,
        on_progress: ProgressCallback | None = None,
        on_safety: SafetyCallback | None = None,
    ) -> RunReport:
        self.profile.validate()
        resolved = resolve_cameras(
            cameras,
            self.profile,
            password=password,
            password_env=password_env,
            username=username,
            environ=self._environ,
        )
        if not resolved:
            raise ConfigError("沒有需要執行的攝影機")

        started_at = _timestamp()
        total = len(resolved)
        results: list[CameraResult | None] = [None] * total
        state = _RunState()
        ratio_enabled = _ratio_enabled(self.profile, total)

        def job(index: int, camera: ResolvedCamera) -> tuple[int, CameraResult, int, str | None]:
            with state.lock:
                already_stopped = state.stop
            if already_stopped:
                result = _skipped_camera(camera, "已觸發失敗率保護，未執行")
                with state.lock:
                    state.finished += 1
                    done = state.finished
                return index, result, done, None

            result = self._apply_camera(camera, dry_run)
            safety_note: str | None = None
            with state.lock:
                state.finished += 1
                state.executed += 1
                if not result.ok:
                    state.failed += 1
                if ratio_enabled and not state.stop and _should_trip(self.profile, state.failed, state.executed):
                    state.stop = True
                    state.safety_tripped = True
                    state.safety_message = _safety_message(self.profile, state.failed, state.executed)
                    safety_note = state.safety_message
                done = state.finished
            return index, result, done, safety_note

        with ThreadPoolExecutor(max_workers=self.profile.concurrency) as executor:
            futures = [executor.submit(job, index, camera) for index, camera in enumerate(resolved)]
            for future in as_completed(futures):
                index, result, done, safety_note = future.result()
                results[index] = result
                if safety_note is not None and on_safety is not None:
                    on_safety(safety_note)
                if on_progress is not None:
                    on_progress(result, done, total)

        finished = [item for item in results if item is not None]
        if len(finished) != total:
            raise ConfigError("執行結果不完整")
        return RunReport(
            profile=self.profile.name,
            dry_run=dry_run,
            started_at=started_at,
            finished_at=_timestamp(),
            safety_tripped=state.safety_tripped,
            safety_message=state.safety_message,
            cameras=finished,
        )

    def _apply_camera(self, camera: ResolvedCamera, dry_run: bool) -> CameraResult:
        started = time.perf_counter()
        steps: list[StepResult] = []
        abort = False
        client: SupportsIsapi | None = None
        try:
            client = self._client_factory(camera, self.profile)
            for step in self.profile.steps:
                if abort:
                    steps.append(
                        StepResult(
                            id=step.id,
                            ok=False,
                            skipped=True,
                            path=step.path,
                            note="前一步失敗，已略過",
                        )
                    )
                    continue
                result = self._run_step(client, camera, step, dry_run)
                steps.append(result)
                if not result.ok and step.on_error == "abort_camera":
                    abort = True
        except ConfigError:
            raise
        except Exception as exc:
            if isinstance(exc, AssertionError):
                raise
            failed = _first_failure(steps)
            return CameraResult(
                id=camera.id,
                host=camera.host,
                ok=False,
                skipped=False,
                elapsed_ms=(time.perf_counter() - started) * 1000,
                error=failed.error if failed else f"內部錯誤：{exc}",
                failed_step=failed.id if failed else None,
                steps=steps,
            )
        finally:
            if client is not None:
                client.close()

        failed = _first_failure(steps)
        ok = bool(steps) and all(step.ok and not step.skipped for step in steps)
        return CameraResult(
            id=camera.id,
            host=camera.host,
            ok=ok,
            skipped=False,
            elapsed_ms=(time.perf_counter() - started) * 1000,
            error=None if failed is None else failed.error,
            failed_step=None if failed is None else failed.id,
            steps=steps,
        )

    def _run_step(self, client: SupportsIsapi, camera: ResolvedCamera, step: Step, dry_run: bool) -> StepResult:
        started = time.perf_counter()
        try:
            if step.mode == "request":
                result = self._request(client, camera, step, dry_run)
            elif step.mode == "merge_xml":
                result = self._merge_xml(client, camera, step, dry_run)
            elif step.mode == "assert_xml":
                result = self._assert_xml(client, camera, step)
            else:
                raise ConfigError(f"step {step.id} 的 mode 不正確")
        except TemplateError as exc:
            result = StepResult(id=step.id, ok=False, method=step.method, path=step.path, error=str(exc))
        except XmlPathError as exc:
            result = StepResult(id=step.id, ok=False, method=step.method, path=step.path, error=str(exc))
        result.elapsed_ms = (time.perf_counter() - started) * 1000
        return result

    def _request(self, client: SupportsIsapi, camera: ResolvedCamera, step: Step, dry_run: bool) -> StepResult:
        path = self._render_path(step, camera)
        headers = self._render_headers(step, camera)
        content = self._render_body(step, camera)
        if content is not None:
            headers.setdefault("Content-Type", step.content_type)
        if dry_run and step.method in _MUTATING:
            return StepResult(
                id=step.id,
                ok=True,
                sent=False,
                dry_run=True,
                method=step.method,
                path=path,
                note="演練模式，未送出",
            )
        response, attempts, error = self._call(client, step.method, path, content, headers)
        return _from_http(step, method=step.method, path=path, response=response, attempts=attempts, sent=True, error=error)

    def _merge_xml(self, client: SupportsIsapi, camera: ResolvedCamera, step: Step, dry_run: bool) -> StepResult:
        path = self._render_path(step, camera)
        headers = self._render_headers(step, camera)
        assignments = [
            (item_path, render(value, camera=camera.template_fields(), variables=self.profile.vars, environ=self._environ))
            for item_path, value in step.set_values
        ]
        response, attempts, error = self._call(client, "GET", path, None, headers)
        if response is None or not response.ok:
            return _from_http(step, method="GET", path=path, response=response, attempts=attempts, sent=True, error=error)
        try:
            root = parse_xml(response.body)
        except ET.ParseError:
            return StepResult(
                id=step.id,
                ok=False,
                sent=True,
                method="GET",
                path=path,
                http_status=response.http_status,
                attempts=attempts,
                error="回應不是合法的 XML",
                body=_truncate(response.body, 4000),
            )

        edit = edit_xml(
            root,
            set_values=assignments,
            remove_paths=list(step.remove_paths),
            on_missing=step.on_missing,
        )
        if not edit.changes and not step.force:
            return StepResult(
                id=step.id,
                ok=True,
                sent=False,
                method="PUT",
                path=path,
                http_status=response.http_status,
                attempts=attempts,
                note="沒有差異，未寫入",
                warnings=edit.warnings,
            )
        if dry_run:
            return StepResult(
                id=step.id,
                ok=True,
                sent=False,
                dry_run=True,
                method="PUT",
                path=path,
                http_status=response.http_status,
                attempts=attempts,
                note="演練模式，未送出",
                warnings=edit.warnings,
                changes=edit.changes,
            )

        payload = serialize(root)
        put_headers = dict(headers)
        put_headers.setdefault("Content-Type", step.content_type)
        put_response, put_attempts, put_error = self._call(client, "PUT", path, payload, put_headers)
        result = _from_http(
            step,
            method="PUT",
            path=path,
            response=put_response,
            attempts=attempts + put_attempts,
            sent=True,
            error=put_error,
        )
        result.warnings = edit.warnings
        result.changes = edit.changes
        return result

    def _assert_xml(self, client: SupportsIsapi, camera: ResolvedCamera, step: Step) -> StepResult:
        path = self._render_path(step, camera)
        headers = self._render_headers(step, camera)
        response, attempts, error = self._call(client, "GET", path, None, headers)
        if response is None or not response.ok:
            return _from_http(step, method="GET", path=path, response=response, attempts=attempts, sent=True, error=error)
        try:
            root = parse_xml(response.body)
        except ET.ParseError:
            return StepResult(
                id=step.id,
                ok=False,
                sent=True,
                method="GET",
                path=path,
                http_status=response.http_status,
                attempts=attempts,
                error="回應不是合法的 XML",
                body=_truncate(response.body, 4000),
            )

        mismatches: list[str] = []
        for item_path, expected_raw in step.expect_values:
            expected = render(
                expected_raw,
                camera=camera.template_fields(),
                variables=self.profile.vars,
                environ=self._environ,
            ).strip()
            try:
                node = find_node(root, item_path, create=False)
            except XmlPathError:
                mismatches.append(f"{item_path} 不存在，預期 {expected}")
                continue
            if any(isinstance(child.tag, str) for child in list(node)):
                mismatches.append(f"{item_path} 不是文字節點")
                continue
            actual = (node.text or "").strip()
            if actual != expected:
                mismatches.append(f"{item_path} 預期 {expected}，實際 {actual}")
        if mismatches:
            return StepResult(
                id=step.id,
                ok=False,
                sent=True,
                method="GET",
                path=path,
                http_status=response.http_status,
                attempts=attempts,
                error="；".join(mismatches),
            )
        return StepResult(
            id=step.id,
            ok=True,
            sent=True,
            method="GET",
            path=path,
            http_status=response.http_status,
            attempts=attempts,
            note="與預期一致",
        )

    def _render_path(self, step: Step, camera: ResolvedCamera) -> str:
        path = render(step.path, camera=camera.template_fields(), variables=self.profile.vars, environ=self._environ)
        if not path.startswith("/"):
            raise TemplateError(f"step {step.id} 的 path 必須以 / 開頭")
        return path

    def _render_headers(self, step: Step, camera: ResolvedCamera) -> dict[str, str]:
        fields = camera.template_fields()
        return {
            key: render(value, camera=fields, variables=self.profile.vars, environ=self._environ)
            for key, value in step.headers
        }

    def _render_body(self, step: Step, camera: ResolvedCamera) -> bytes | None:
        if step.body is None:
            return None
        text = render(step.body, camera=camera.template_fields(), variables=self.profile.vars, environ=self._environ)
        return text.strip().encode("utf-8")

    def _call(
        self,
        client: SupportsIsapi,
        method: str,
        path: str,
        content: bytes | None,
        headers: dict[str, str],
    ) -> tuple[IsapiResponse | None, int, str | None]:
        attempts = 0
        while True:
            attempts += 1
            try:
                response = client.request(method, path, content=content, headers=headers)
            except TransportError as exc:
                if attempts <= self.profile.retries:
                    self._sleep(self._backoff(attempts))
                    continue
                return None, attempts, str(exc)
            if response.ok or not response.retryable or attempts > self.profile.retries:
                return response, attempts, None
            self._sleep(self._backoff(attempts))

    def _backoff(self, attempt: int) -> float:
        return min(10.0, self.profile.retry_backoff_seconds * attempt)


def default_client_factory(camera: ResolvedCamera, profile: Profile) -> IsapiClient:
    return IsapiClient(
        host=camera.host,
        port=camera.port,
        username=camera.username,
        password=camera.password,
        use_https=camera.use_https,
        verify_tls=profile.verify_tls,
        timeout=profile.timeout_seconds,
        user_agent=profile.user_agent,
    )


def resolve_cameras(
    cameras: Sequence[Camera],
    profile: Profile,
    *,
    password: str | None = None,
    password_env: str | None = None,
    username: str | None = None,
    environ: Mapping[str, str] | None = None,
) -> list[ResolvedCamera]:
    """補齊每支攝影機的帳號、密碼與埠。清單裡的密碼優先於共用密碼。"""
    source = environ if environ is not None else os.environ
    env_name = password_env or profile.password_env
    shared = password if password else (source.get(env_name) or "")
    missing: list[str] = []
    resolved: list[ResolvedCamera] = []
    for camera in cameras:
        use_https = profile.use_https if camera.use_https is None else camera.use_https
        port = camera.port if camera.port is not None else (443 if use_https else 80)
        user = camera.username or username or profile.username
        secret = camera.password or shared
        if not secret:
            missing.append(camera.id)
            continue
        resolved.append(
            ResolvedCamera(
                id=camera.id,
                host=camera.host,
                name=camera.name,
                port=port,
                username=user,
                use_https=use_https,
                tags=camera.tags,
                attributes=dict(camera.attributes),
                password=secret,
            )
        )
    if missing:
        raise ConfigError(
            f"這些攝影機沒有密碼（可在清單填 password，或設定環境變數 {env_name}）：{preview_ids(missing)}"
        )
    return resolved


def _from_http(
    step: Step,
    *,
    method: str,
    path: str,
    response: IsapiResponse | None,
    attempts: int,
    sent: bool,
    error: str | None,
) -> StepResult:
    if response is None:
        return StepResult(
            id=step.id,
            ok=False,
            sent=sent,
            method=method,
            path=path,
            attempts=attempts,
            error=error or "連線失敗",
        )
    ok = response.ok and not error
    note = None
    if response.reboot_required and response.ok:
        note = "裝置要求重新開機後設定才會完全生效"
    body = None
    if step.capture_body or not ok:
        limit = 100_000 if step.capture_body and ok else 4_000
        body = _truncate(response.body, limit)
    return StepResult(
        id=step.id,
        ok=ok,
        sent=sent,
        method=method,
        path=path,
        http_status=response.http_status,
        isapi_status_code=response.isapi_status_code,
        reboot_required=response.reboot_required and response.ok,
        attempts=attempts,
        error=None if ok else (error or response.error or f"HTTP {response.http_status}"),
        note=note,
        body=body,
    )


def _first_failure(steps: Sequence[StepResult]) -> StepResult | None:
    for step in steps:
        if not step.ok and not step.skipped:
            return step
    return None


def _skipped_camera(camera: ResolvedCamera, note: str) -> CameraResult:
    return CameraResult(
        id=camera.id,
        host=camera.host,
        ok=False,
        skipped=True,
        elapsed_ms=0,
        error=note,
        failed_step=None,
        steps=[],
        note=note,
    )


def _ratio_enabled(profile: Profile, total: int) -> bool:
    ratio = profile.max_failure_ratio
    return ratio is not None and ratio < 1 and total >= profile.safety_min_samples


def _should_trip(profile: Profile, failed: int, executed: int) -> bool:
    ratio = profile.max_failure_ratio
    if ratio is None or ratio >= 1 or executed < profile.safety_min_samples:
        return False
    if ratio <= 0:
        return failed > 0
    return failed / executed >= ratio


def _safety_message(profile: Profile, failed: int, executed: int) -> str:
    ratio = profile.max_failure_ratio
    if ratio is None or ratio <= 0:
        threshold = "出現失敗即停止"
    else:
        threshold = f"門檻 {ratio:.0%}"
    actual = 0 if executed == 0 else failed / executed
    return (
        f"失敗率保護已啟動：完成 {executed} 支時有 {failed} 支失敗（{actual:.0%}），"
        f"{threshold}，已停止其餘攝影機"
    )


def _timestamp() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _truncate(text: str, limit: int) -> str:
    if len(text) <= limit:
        return text
    return text[:limit] + "\n...（已截斷）"
