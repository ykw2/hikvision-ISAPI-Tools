import httpx
import pytest

from hik_isapi.client import IsapiClient, interpret_response
from hik_isapi.errors import TransportError

NS = "http://www.hikvision.com/ver20/XMLSchema"
DEVICE = f"<DeviceInfo xmlns='{NS}'><model>DS-TEST</model></DeviceInfo>"
OK = f"""<ResponseStatus xmlns="{NS}">
  <statusCode>1</statusCode>
  <statusString>OK</statusString>
  <subStatusCode>ok</subStatusCode>
</ResponseStatus>"""


def test_interpret_success_reboot_busy_and_auth() -> None:
    info = interpret_response("GET", "/ISAPI/System/deviceInfo", 200, DEVICE, 3)
    assert info.ok
    assert info.isapi_status_code is None

    reboot = interpret_response(
        "PUT",
        "/ISAPI/System/time",
        200,
        OK.replace(">1<", ">7<").replace("OK", "Reboot Required"),
        3,
    )
    assert reboot.ok
    assert reboot.reboot_required
    assert reboot.isapi_status_code == 7

    busy = interpret_response("PUT", "/x", 200, OK.replace(">1<", ">2<"), 3)
    assert not busy.ok
    assert busy.retryable
    assert "2" in (busy.error or "")

    invalid = interpret_response("PUT", "/x", 200, OK.replace(">1<", ">6<"), 3)
    assert not invalid.ok
    assert not invalid.retryable

    denied = interpret_response("GET", "/x", 401, "", 3)
    assert not denied.ok
    assert "digest" in (denied.error or "")
    assert not denied.retryable

    unavailable = interpret_response("GET", "/x", 503, "", 3)
    assert not unavailable.ok
    assert unavailable.retryable


def test_client_sends_put_body_and_digest() -> None:
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        if request.method == "GET" and "Authorization" not in request.headers:
            return httpx.Response(
                401,
                headers={
                    "WWW-Authenticate": 'Digest realm="IP Camera", nonce="abc", qop="auth", algorithm=MD5'
                },
            )
        if request.method == "PUT":
            assert request.content == b"<Time/>"
            assert request.headers["content-type"] == "application/xml"
            return httpx.Response(200, text=OK)
        assert request.headers["Authorization"].startswith("Digest ")
        return httpx.Response(200, text=DEVICE)

    transport = httpx.MockTransport(handler)
    with IsapiClient(
        host="10.0.0.8",
        port=80,
        username="admin",
        password="secret",
        transport=transport,
    ) as client:
        probed = client.request("GET", "/ISAPI/System/deviceInfo")
        updated = client.request("PUT", "/ISAPI/System/time", content=b"<Time/>", headers={"Content-Type": "application/xml"})

    assert probed.ok
    assert updated.ok
    assert updated.isapi_status_code == 1
    assert any(request.url.path == "/ISAPI/System/deviceInfo" for request in seen)


def test_client_wraps_connection_errors() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused")

    with IsapiClient(host="10.0.0.9", port=80, username="admin", password="secret", transport=httpx.MockTransport(handler)) as client:
        with pytest.raises(TransportError, match="連線失敗"):
            client.request("GET", "/ISAPI/System/deviceInfo")
