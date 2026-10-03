"""測試用的假 ISAPI 連線。"""

from __future__ import annotations

from collections.abc import Callable

from hik_isapi.client import IsapiResponse

Responder = Callable[[str, str, bytes | None, dict[str, str]], IsapiResponse]


class FakeClient:
    def __init__(self, responder: Responder) -> None:
        self.responder = responder
        self.calls: list[dict[str, object]] = []
        self.closed = False

    def request(
        self,
        method: str,
        path: str,
        content: bytes | None = None,
        headers: dict[str, str] | None = None,
    ) -> IsapiResponse:
        sent_headers = dict(headers or {})
        self.calls.append({"method": method, "path": path, "content": content, "headers": sent_headers})
        return self.responder(method, path, content, sent_headers)

    def close(self) -> None:
        self.closed = True


def http_response(
    *,
    status: int = 200,
    body: str = "",
    ok: bool = True,
    retryable: bool = False,
    error: str | None = None,
    code: int | None = None,
    reboot: bool = False,
) -> IsapiResponse:
    return IsapiResponse(
        method="GET",
        path="/",
        http_status=status,
        body=body,
        elapsed_ms=1,
        ok=ok,
        retryable=retryable,
        error=error,
        isapi_status_code=code,
        isapi_status_string=None,
        isapi_sub_status=None,
        reboot_required=reboot,
    )
