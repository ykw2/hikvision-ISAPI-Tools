"""海康 ISAPI 用戶端。使用 HTTP Digest，這是攝影機 Web 認證最常見的方式。"""

from __future__ import annotations

import time
from dataclasses import dataclass
from xml.etree import ElementTree as ET

import httpx

from hik_isapi.errors import TransportError
from hik_isapi.version import __version__
from hik_isapi.xmlutil import direct_text, local_name, parse_xml

_RETRYABLE_HTTP = {408, 500, 502, 503, 504}


@dataclass(frozen=True)
class ResponseStatus:
    code: int | None
    status_string: str
    sub_status: str


@dataclass(frozen=True)
class IsapiResponse:
    method: str
    path: str
    http_status: int
    body: str
    elapsed_ms: float
    ok: bool
    retryable: bool
    error: str | None
    isapi_status_code: int | None
    isapi_status_string: str | None
    isapi_sub_status: str | None
    reboot_required: bool


class IsapiClient:
    """對單一支攝影機關一次連線。請用 with 或 close() 關掉。"""

    def __init__(
        self,
        *,
        host: str,
        port: int,
        username: str,
        password: str,
        use_https: bool = False,
        verify_tls: bool = False,
        timeout: float = 20.0,
        user_agent: str | None = None,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        scheme = "https" if use_https else "http"
        self._base = f"{scheme}://{host}:{port}"
        connect = min(10.0, float(timeout))
        self._client = httpx.Client(
            auth=httpx.DigestAuth(username, password),
            verify=verify_tls,
            timeout=httpx.Timeout(timeout, connect=connect),
            trust_env=False,
            follow_redirects=False,
            transport=transport,
            headers={
                "User-Agent": user_agent or f"hik-isapi/{__version__}",
                "Accept": "application/xml, application/json;q=0.9, */*;q=0.8",
            },
        )

    def __enter__(self) -> IsapiClient:
        return self

    def __exit__(self, *args: object) -> None:
        self.close()

    def close(self) -> None:
        self._client.close()

    def request(
        self,
        method: str,
        path: str,
        content: bytes | None = None,
        headers: dict[str, str] | None = None,
    ) -> IsapiResponse:
        relative = path if path.startswith("/") else f"/{path}"
        started = time.perf_counter()
        try:
            response = self._client.request(
                method.upper(),
                self._base + relative,
                content=content,
                headers=headers,
            )
        except httpx.HTTPError as exc:
            raise TransportError(f"連線失敗 {self._base}：{exc}") from exc
        elapsed_ms = (time.perf_counter() - started) * 1000
        return interpret_response(method.upper(), relative, response.status_code, response.text, elapsed_ms)


def interpret_response(method: str, path: str, http_status: int, body: str, elapsed_ms: float) -> IsapiResponse:
    """依 HTTP 狀態與 ResponseStatus XML 判斷這次 ISAPI 是否成功。"""
    status = _parse_response_status(body)
    reboot_required = status is not None and status.code == 7
    retryable = http_status in _RETRYABLE_HTTP or (status is not None and status.code == 2)
    error: str | None = None
    ok = False

    if http_status == 401:
        error = "認證失敗（HTTP 401）。請確認帳號密碼，以及攝影機的 Web 認證方式為 digest"
    elif http_status == 403:
        error = "沒有權限（HTTP 403）"
    elif status is not None and status.code not in (1, 7):
        error = _format_isapi_error(status)
    elif not 200 <= http_status <= 299:
        error = f"HTTP {http_status}"
    else:
        ok = True

    return IsapiResponse(
        method=method,
        path=path,
        http_status=http_status,
        body=body,
        elapsed_ms=elapsed_ms,
        ok=ok,
        retryable=retryable,
        error=error,
        isapi_status_code=None if status is None else status.code,
        isapi_status_string=None if status is None else status.status_string or None,
        isapi_sub_status=None if status is None else status.sub_status or None,
        reboot_required=reboot_required,
    )


def _parse_response_status(body: str) -> ResponseStatus | None:
    if not body or "<" not in body:
        return None
    try:
        root = parse_xml(body)
    except ET.ParseError:
        return None
    if local_name(root.tag) != "ResponseStatus":
        return None
    code_text = direct_text(root, "statusCode")
    code = int(code_text) if code_text and code_text.isdigit() else None
    return ResponseStatus(
        code=code,
        status_string=direct_text(root, "statusString") or "",
        sub_status=direct_text(root, "subStatusCode") or "",
    )


def _format_isapi_error(status: ResponseStatus) -> str:
    parts = ["ISAPI"]
    if status.code is not None:
        parts.append(str(status.code))
    if status.status_string:
        parts.append(status.status_string)
    if status.sub_status:
        parts.append(f"({status.sub_status})")
    return " ".join(parts)
