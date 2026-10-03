import json
from pathlib import Path

import pytest

from hik_isapi.errors import ConfigError
from hik_isapi.inventory import Camera, load_failed_ids
from hik_isapi.profile import Profile, Step
from hik_isapi.runner import Runner, resolve_cameras
from tests.fakes import FakeClient, http_response

NS = "http://www.hikvision.com/ver20/XMLSchema"
TIME_MANUAL = f"""<?xml version="1.0" encoding="UTF-8"?>
<Time version="2.0" xmlns="{NS}"><timeMode>manual</timeMode><timeZone>CST-8:00:00</timeZone></Time>
"""
TIME_NTP = TIME_MANUAL.replace("manual", "NTP")
OVERLAY = f"""<?xml version="1.0" encoding="UTF-8"?>
<VideoOverlay version="2.0" xmlns="{NS}">
  <channelNameOverlay><enabled>true</enabled><channelName>old</channelName></channelNameOverlay>
</VideoOverlay>
"""
STATUS_OK = f"""<ResponseStatus xmlns="{NS}"><statusCode>1</statusCode><statusString>OK</statusString></ResponseStatus>"""


def test_resolve_password_precedence_and_ports() -> None:
    profile = Profile(name="p", steps=(Step(id="probe", path="/ISAPI/System/deviceInfo"),), use_https=True)
    cameras = [
        Camera(id="row", host="10.0.0.1", name="row", password="from-row", port=8080, use_https=False),
        Camera(id="shared", host="10.0.0.2", name="shared"),
        Camera(id="named", host="10.0.0.3", name="大門", username="operator"),
    ]
    resolved = resolve_cameras(cameras, profile, password="from-cli", username="admin", environ={})
    by_id = {camera.id: camera for camera in resolved}
    assert by_id["row"].password == "from-row"
    assert by_id["row"].port == 8080
    assert by_id["row"].use_https is False
    assert by_id["shared"].password == "from-cli"
    assert by_id["shared"].port == 443
    assert by_id["shared"].use_https is True
    assert by_id["named"].username == "operator"
    assert "from-cli" not in repr(by_id["shared"])

    with pytest.raises(ConfigError, match="no-secret"):
        resolve_cameras(
            [Camera(id="no-secret", host="10.0.0.4", name="x")],
            profile,
            environ={},
        )


def test_merge_put_dry_run_and_idempotent() -> None:
    clients: dict[str, FakeClient] = {}

    def factory(camera, profile):
        def respond(method, path, content, headers):
            if method == "GET":
                return http_response(body=TIME_MANUAL)
            return http_response(body=STATUS_OK, code=1)
        client = FakeClient(respond)
        clients[camera.id] = client
        return client

    profile = Profile(
        name="time",
        steps=(Step(id="timezone", path="/ISAPI/System/time", mode="merge_xml", method="PUT", set_values=(("timeMode", "NTP"),)),),
        retries=0,
        concurrency=1,
    )
    camera = Camera(id="cam-1", host="10.0.0.11", name="大門", password="secret")
    report = Runner(profile, client_factory=factory, environ={}).run([camera])
    assert report.success_count == 1
    assert report.exit_code == 0
    put = [call for call in clients["cam-1"].calls if call["method"] == "PUT"]
    assert len(put) == 1
    body = put[0]["content"]
    assert isinstance(body, bytes)
    assert b"NTP" in body
    assert b"ns0" not in body
    assert f'xmlns="{NS}"'.encode() in body
    assert clients["cam-1"].closed
    assert report.cameras[0].steps[0].changes[0]["before"] == "manual"

    def already(method, path, content, headers):
        assert method == "GET"
        return http_response(body=TIME_NTP)

    same = FakeClient(already)

    def same_factory(camera, profile):
        return same

    again = Runner(profile, client_factory=same_factory, environ={}).run([camera])
    assert again.cameras[0].steps[0].note == "沒有差異，未寫入"
    assert again.cameras[0].steps[0].sent is False
    assert [call["method"] for call in same.calls] == ["GET"]

    def dry_factory(camera, profile):
        return FakeClient(lambda method, path, content, headers: http_response(body=TIME_MANUAL))

    dry = Runner(profile, client_factory=dry_factory, environ={}).run([camera], dry_run=True)
    assert dry.dry_run
    assert dry.cameras[0].steps[0].dry_run
    assert dry.cameras[0].steps[0].sent is False
    assert dry.cameras[0].ok


def test_names_are_rendered_per_camera() -> None:
    clients: dict[str, FakeClient] = {}

    def factory(camera, profile):
        def respond(method, path, content, headers):
            if method == "GET":
                return http_response(body=OVERLAY)
            text = content.decode() if content else ""
            assert camera.name in text
            return http_response(body=STATUS_OK, code=1)
        client = FakeClient(respond)
        clients[camera.id] = client
        return client

    profile = Profile(
        name="osd",
        steps=(
            Step(
                id="osd",
                path="/ISAPI/System/Video/inputs/channels/1/overlays",
                mode="merge_xml",
                method="PUT",
                set_values=(("channelNameOverlay/channelName", "{{camera.name}}"),),
            ),
        ),
        retries=0,
        concurrency=2,
    )
    cameras = [
        Camera(id="a", host="10.0.0.1", name="大門", password="secret"),
        Camera(id="b", host="10.0.0.2", name="倉庫", password="secret", attributes={"site": "B棟"}),
    ]
    report = Runner(profile, client_factory=factory, environ={}).run(cameras)
    assert report.success_count == 2
    assert "大門".encode() in clients["a"].calls[-1]["content"]
    assert "倉庫".encode() in clients["b"].calls[-1]["content"]


def test_abort_continue_retry_and_path_template() -> None:
    calls: list[str] = []

    def respond(method, path, content, headers):
        calls.append(path)
        return http_response(status=401, ok=False, error="認證失敗")

    profile = Profile(
        name="abort",
        steps=(
            Step(id="probe", path="/ISAPI/System/deviceInfo", on_error="abort_camera"),
            Step(id="later", path="/ISAPI/System/time"),
        ),
        retries=0,
        concurrency=1,
    )
    camera = Camera(id="cam", host="10.0.0.5", name="cam", password="secret")
    report = Runner(profile, client_factory=lambda camera, profile: FakeClient(respond), environ={}).run([camera])
    assert calls == ["/ISAPI/System/deviceInfo"]
    assert report.cameras[0].steps[1].skipped
    assert report.cameras[0].failed_step == "probe"
    assert report.failed_count == 1

    seen: list[str] = []

    def both(method, path, content, headers):
        seen.append(path)
        if path.endswith("deviceInfo"):
            return http_response(status=401, ok=False, error="認證失敗")
        return http_response(body=TIME_NTP)

    continued = Profile(
        name="continue",
        steps=(
            Step(id="probe", path="/ISAPI/System/deviceInfo", on_error="continue"),
            Step(
                id="check",
                path="/ISAPI/System/time",
                mode="assert_xml",
                method="GET",
                expect_values=(("timeMode", "NTP"),),
            ),
        ),
        retries=0,
    )
    result = Runner(continued, client_factory=lambda camera, profile: FakeClient(both), environ={}).run([camera])
    assert seen == ["/ISAPI/System/deviceInfo", "/ISAPI/System/time"]
    assert result.cameras[0].steps[1].ok
    assert not result.cameras[0].ok

    attempts = {"n": 0}
    sleeps: list[float] = []

    def flaky(method, path, content, headers):
        attempts["n"] += 1
        if attempts["n"] == 1:
            return http_response(status=503, ok=False, retryable=True, error="HTTP 503")
        return http_response(body="<DeviceInfo><model>X</model></DeviceInfo>")

    retried = Profile(
        name="retry",
        steps=(Step(id="probe", path="/ISAPI/Streaming/channels/{{camera.channel}}"),),
        retries=2,
        retry_backoff_seconds=0.5,
        concurrency=1,
    )
    channel_cam = Camera(id="cam", host="10.0.0.5", name="cam", password="secret", attributes={"channel": "101"})
    retried_report = Runner(
        retried,
        client_factory=lambda camera, profile: FakeClient(flaky),
        sleep=sleeps.append,
        environ={},
    ).run([channel_cam])
    assert retried_report.cameras[0].ok
    assert retried_report.cameras[0].steps[0].attempts == 2
    assert retried_report.cameras[0].steps[0].path == "/ISAPI/Streaming/channels/101"
    assert sleeps == [0.5]


def test_assert_mismatch_and_reboot() -> None:
    def mismatch(method, path, content, headers):
        return http_response(body=TIME_MANUAL)

    profile = Profile(
        name="assert",
        steps=(
            Step(
                id="verify",
                path="/ISAPI/System/time",
                mode="assert_xml",
                method="GET",
                expect_values=(("timeMode", "NTP"),),
            ),
        ),
        retries=0,
    )
    camera = Camera(id="cam", host="10.0.0.6", name="cam", password="secret")
    report = Runner(profile, client_factory=lambda camera, profile: FakeClient(mismatch), environ={}).run([camera])
    assert "預期 NTP" in (report.cameras[0].error or "")
    assert "manual" in (report.cameras[0].error or "")

    def reboot(method, path, content, headers):
        if method == "GET":
            return http_response(body=TIME_MANUAL)
        return http_response(body=STATUS_OK.replace(">1<", ">7<"), ok=True, code=7, reboot=True)

    writing = Profile(
        name="reboot",
        steps=(Step(id="timezone", path="/ISAPI/System/time", mode="merge_xml", method="PUT", set_values=(("timeMode", "NTP"),)),),
        retries=0,
    )
    done = Runner(writing, client_factory=lambda camera, profile: FakeClient(reboot), environ={}).run([camera])
    assert done.cameras[0].ok
    assert done.cameras[0].reboot_required
    assert "重新開機" in (done.cameras[0].steps[0].note or "")


def test_safety_breaker_is_deterministic_when_serial() -> None:
    cameras = [Camera(id=f"c{i:02d}", host=f"10.0.0.{i}", name=f"c{i:02d}", password="secret") for i in range(15)]
    profile = Profile(
        name="safety",
        steps=(Step(id="probe", path="/ISAPI/System/deviceInfo"),),
        concurrency=1,
        retries=0,
        max_failure_ratio=0.2,
        safety_min_samples=5,
    )
    created: list[str] = []

    def factory(camera, profile):
        created.append(camera.id)
        return FakeClient(lambda method, path, content, headers: http_response(status=401, ok=False, error="認證失敗"))

    report = Runner(profile, client_factory=factory, environ={}).run(cameras)
    assert report.safety_tripped
    assert created == [f"c{i:02d}" for i in range(5)]
    assert report.failed_count == 5
    assert report.skipped_count == 10
    assert report.cameras[5].skipped
    assert report.exit_code == 1

    mixed = [Camera(id=f"m{i:02d}", host=f"10.1.0.{i}", name=f"m{i:02d}", password="secret") for i in range(10)]
    ran: list[str] = []

    def mixed_factory(camera, profile):
        ran.append(camera.id)

        def respond(method, path, content, headers):
            if camera.id == "m00":
                return http_response(status=401, ok=False, error="認證失敗")
            return http_response(body="<DeviceInfo><model>X</model></DeviceInfo>")

        return FakeClient(respond)

    ratio = Profile(
        name="ratio",
        steps=(Step(id="probe", path="/ISAPI/System/deviceInfo"),),
        concurrency=1,
        retries=0,
        max_failure_ratio=0.2,
        safety_min_samples=5,
    )
    mixed_report = Runner(ratio, client_factory=mixed_factory, environ={}).run(mixed)
    assert ran == [f"m{i:02d}" for i in range(5)]
    assert mixed_report.skipped_count == 5

    small = cameras[:4]
    small_profile = Profile(
        name="pilot",
        steps=(Step(id="probe", path="/ISAPI/System/deviceInfo"),),
        concurrency=1,
        retries=0,
        max_failure_ratio=0.2,
        safety_min_samples=20,
    )
    pilot = Runner(
        small_profile,
        client_factory=lambda camera, profile: FakeClient(
            lambda method, path, content, headers: http_response(status=401, ok=False, error="認證失敗")
        ),
        environ={},
    ).run(small)
    assert not pilot.safety_tripped
    assert pilot.failed_count == 4
    assert pilot.skipped_count == 0


def test_report_roundtrip_marks_failures(tmp_path: Path) -> None:
    ok = Camera(id="ok", host="10.0.0.1", name="ok", password="secret")
    bad = Camera(id="bad", host="10.0.0.2", name="bad", password="secret")

    def factory(camera, profile):
        def respond(method, path, content, headers):
            if camera.id == "bad":
                return http_response(status=401, ok=False, error="認證失敗")
            return http_response(body="<DeviceInfo><model>X</model><deviceName>大門</deviceName></DeviceInfo>")

        return FakeClient(respond)

    profile = Profile(
        name="report",
        steps=(Step(id="probe", path="/ISAPI/System/deviceInfo", capture_body=True),),
        retries=0,
        max_failure_ratio=1,
        concurrency=1,
    )
    report = Runner(profile, client_factory=factory, environ={}).run([ok, bad])
    json_path, csv_path = report.write(tmp_path / "run.json")
    assert json_path.exists()
    assert csv_path.read_bytes().startswith(b"\xef\xbb\xbf")
    payload = json.loads(json_path.read_text(encoding="utf-8"))
    assert payload["success_count"] == 1
    assert payload["cameras"][0]["steps"][0]["body"]
    assert load_failed_ids(json_path) == {"bad"}


def test_request_dry_run_does_not_send_put() -> None:
    called = {"n": 0}

    def respond(method, path, content, headers):
        called["n"] += 1
        return http_response()

    profile = Profile(
        name="put",
        steps=(Step(id="put_ntp", path="/ISAPI/System/time/ntpServers/1", method="PUT", body="<NTPServer/>"),),
        retries=0,
    )
    camera = Camera(id="cam", host="10.0.0.7", name="cam", password="secret")
    report = Runner(profile, client_factory=lambda camera, profile: FakeClient(respond), environ={}).run([camera], dry_run=True)
    assert called["n"] == 0
    assert report.cameras[0].ok
    assert report.cameras[0].steps[0].note == "演練模式，未送出"
