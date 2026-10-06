"""海康收件、管理頁與轉送。下游用假的 HTTP，不連 Cloudflare。"""

from __future__ import annotations

import asyncio
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx
import pytest
from fastapi.testclient import TestClient

from app.db import Store, utc_now
from app.forwarder import build_payload, forward_once
from app.main import create_app
from app.parse import parse_body
from app.passwords import (
    SYMBOLS,
    acceptable_new_password,
    ensure_gui_password,
    generate_password,
    verify_password,
    write_password_hash,
)
from app.settings import Settings

ROOT = Path(__file__).resolve().parents[1]
WEBHOOK = ROOT / "deploy" / "webhook"
PASSWORD = "InitPassw0rd!"


def xml(event_type: str = "VMD", when: str = "2026-10-06T12:00:00+08:00", channel: str = "1") -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<EventNotificationAlert version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
  <ipAddress>10.1.0.11</ipAddress>
  <channelID>{channel}</channelID>
  <dateTime>{when}</dateTime>
  <eventType>{event_type}</eventType>
  <eventState>active</eventState>
  <eventDescription>閘門</eventDescription>
</EventNotificationAlert>
"""


def make_app(tmp_path: Path, password: str = PASSWORD, **kwargs) -> TestClient:
    data = tmp_path / "data"
    data.mkdir(parents=True)
    write_password_hash(data / "gui_password.hash", password)
    settings = Settings(
        data_dir=data,
        database_path=data / "events.db",
        run_forwarder=False,
        **kwargs,
    )
    return TestClient(create_app(settings))


def csrf_from(page: str) -> str:
    match = re.search(r'name="csrf" value="([^"]+)"', page)
    assert match is not None
    return match.group(1)


def login(client: TestClient, password: str = PASSWORD) -> None:
    response = client.post("/login", data={"password": password}, follow_redirects=False)
    assert response.status_code == 303


def test_generated_password_is_complex() -> None:
    for _ in range(20):
        password = generate_password()
        assert len(password) == 24
        assert any(char.isupper() for char in password)
        assert any(char.islower() for char in password)
        assert any(char.isdigit() for char in password)
        assert any(char in SYMBOLS for char in password)


def test_new_password_only_needs_six_characters() -> None:
    assert acceptable_new_password("abcde") is False
    assert acceptable_new_password("abcdef") is True
    assert acceptable_new_password("  abc  ") is False


def test_initial_password_is_printed_once_and_hashed(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    path = tmp_path / "gui_password.hash"
    created = ensure_gui_password(path, print_initial=True)
    printed = capsys.readouterr().out
    assert created is not None
    assert f"管理頁初始密碼：{created}" in printed
    assert created not in path.read_text(encoding="utf-8")
    assert path.read_text(encoding="utf-8").startswith("scrypt$")
    assert verify_password(path.read_text(encoding="utf-8"), created)
    assert ensure_gui_password(path, print_initial=True) is None
    assert capsys.readouterr().out == ""


def test_password_cli_prints_once(tmp_path: Path) -> None:
    env = os.environ.copy()
    env["DATA_DIR"] = str(tmp_path)
    env["DATABASE_PATH"] = str(tmp_path / "events.db")
    first = subprocess.run(
        [sys.executable, "-m", "app.passwords"],
        cwd=WEBHOOK,
        env=env,
        capture_output=True,
        text=True,
        check=True,
    )
    assert "管理頁初始密碼：" in first.stdout
    password = first.stdout.strip().split("：", 1)[1]
    second = subprocess.run(
        [sys.executable, "-m", "app.passwords"],
        cwd=WEBHOOK,
        env=env,
        capture_output=True,
        text=True,
        check=True,
    )
    assert second.stdout == ""
    stored = (tmp_path / "gui_password.hash").read_text(encoding="utf-8")
    assert verify_password(stored, password)
    assert password not in stored


def test_xml_post_returns_200_and_dedupes(tmp_path: Path) -> None:
    client = make_app(tmp_path)
    first = client.post("/hik/events", content=xml().encode(), headers={"Content-Type": "application/xml"})
    second = client.post("/hik/events", content=xml().encode(), headers={"Content-Type": "application/xml"})
    assert first.status_code == 200
    assert second.status_code == 200
    body = first.json()
    assert body["duplicate"] is False
    assert second.json() == {"id": body["id"], "duplicate": True}
    assert client.app.state.store.count_events() == 1
    stored = client.app.state.store.get_event(body["id"])
    assert stored["event_type"] == "VMD"
    assert stored["channel"] == "1"
    assert stored["status"] == "pending"
    assert "閘門" in stored["event_xml"]


def test_gb18030_xml_is_decoded(tmp_path: Path) -> None:
    text = xml().replace('encoding="UTF-8"', 'encoding="GB2312"')
    client = make_app(tmp_path)
    response = client.post(
        "/hik/events",
        content=text.encode("gb18030"),
        headers={"Content-Type": "application/xml"},
    )
    assert response.status_code == 200
    stored = client.app.state.store.get_event(response.json()["id"])
    assert "閘門" in stored["event_xml"]


def test_multipart_keeps_image(tmp_path: Path) -> None:
    client = make_app(tmp_path)
    jpeg = b"\xff\xd8\xff\xd9picture"
    response = client.post(
        "/hik/events",
        files={
            "event_log": ("event.xml", xml(when="2026-10-06T12:05:00+08:00"), "application/xml"),
            "Picture": ("gate.jpg", jpeg, "image/jpeg"),
        },
    )
    assert response.status_code == 200
    event_id = response.json()["id"]
    images = client.app.state.store.list_images(event_id, include_data=True)
    assert images[0]["filename"] == "gate.jpg"
    assert images[0]["data"] == jpeg

    anon = TestClient(client.app)
    detail_denied = anon.get(f"/events/{event_id}", follow_redirects=False)
    assert detail_denied.status_code == 303
    assert "閘門" not in detail_denied.text
    image_id = images[0]["id"]
    assert anon.get(f"/events/{event_id}/images/{image_id}").status_code == 401

    login(client)
    page = client.get(f"/events/{event_id}")
    assert page.status_code == 200
    assert "閘門" in page.text
    assert 'class="badge pending"' in page.text
    image = client.get(f"/events/{event_id}/images/{image_id}")
    assert image.status_code == 200
    assert image.content == jpeg


def test_basic_auth_is_optional(tmp_path: Path) -> None:
    open_client = make_app(tmp_path)
    assert open_client.post("/hik/events", content=xml().encode(), headers={"Content-Type": "application/xml"}).status_code == 200

    locked = make_app(tmp_path / "locked", hik_basic_user="cam", hik_basic_password="secret")
    rejected = locked.post("/hik/events", content=xml().encode(), headers={"Content-Type": "application/xml"})
    assert rejected.status_code == 401
    accepted = locked.post(
        "/hik/events",
        content=xml().encode(),
        headers={"Content-Type": "application/xml"},
        auth=("cam", "secret"),
    )
    assert accepted.status_code == 200


def test_pages_require_login_and_can_manage_events(tmp_path: Path) -> None:
    client = make_app(tmp_path)
    posted = client.post("/hik/events", content=xml().encode(), headers={"Content-Type": "application/xml"})
    event_id = posted.json()["id"]
    home = client.get("/")
    assert home.status_code == 200
    assert "登入" in home.text
    assert "VMD" not in home.text
    assert "/static/style.css" in home.text

    style = client.get("/static/style.css")
    assert style.status_code == 200
    assert "#E2231A" in style.text
    assert "#F5C400" in style.text
    assert "#F4F4F4" in style.text
    assert "max-width: 720px" in style.text

    wrong = client.post("/login", data={"password": "nope"}, follow_redirects=False)
    assert wrong.status_code == 401
    login(client)
    listed = client.get("/")
    assert "VMD" in listed.text
    assert 'class="badge pending"' in listed.text

    store = client.app.state.store
    store.mark_sent(event_id, utc_now())
    sent_page = client.get(f"/events/{event_id}")
    assert 'class="badge sent"' in sent_page.text
    assert "重送" not in sent_page.text
    rejected = client.post(
        f"/events/{event_id}/retry",
        data={"csrf": csrf_from(sent_page.text)},
        follow_redirects=False,
    )
    assert rejected.status_code == 200
    assert "不能重送" in rejected.text

    store.mark_failure(event_id, "下游忙碌", utc_now(), max_attempts=1)
    failed_page = client.get(f"/events/{event_id}")
    assert 'class="badge failed"' in failed_page.text
    assert "下游忙碌" in failed_page.text
    retried = client.post(
        f"/events/{event_id}/retry",
        data={"csrf": csrf_from(failed_page.text)},
        follow_redirects=True,
    )
    assert retried.status_code == 200
    assert store.get_event(event_id)["status"] == "pending"
    assert store.get_event(event_id)["attempts"] == 0

    denied = client.post(f"/events/{event_id}/delete", data={"csrf": "nope"})
    assert denied.status_code == 400
    assert store.get_event(event_id) is not None
    deleted = client.post(
        f"/events/{event_id}/delete",
        data={"csrf": csrf_from(retried.text)},
        follow_redirects=True,
    )
    assert deleted.status_code == 200
    assert "還沒有收到事件" in deleted.text
    assert store.get_event(event_id) is None


def test_password_can_be_changed_after_login(tmp_path: Path) -> None:
    client = make_app(tmp_path)
    login(client)
    form = client.get("/password")
    token = csrf_from(form.text)
    too_short = client.post(
        "/password",
        data={"csrf": token, "current_password": PASSWORD, "new_password": "abcde"},
    )
    assert too_short.status_code == 200
    assert "至少要 6 字元" in too_short.text
    path = client.app.state.settings.password_path
    assert verify_password(path.read_text(encoding="utf-8"), PASSWORD)

    wrong_current = client.post(
        "/password",
        data={"csrf": csrf_from(too_short.text), "current_password": "other", "new_password": "abcdef"},
    )
    assert "目前密碼不正確" in wrong_current.text

    changed = client.post(
        "/password",
        data={"csrf": csrf_from(wrong_current.text), "current_password": PASSWORD, "new_password": "abcdef"},
        follow_redirects=True,
    )
    assert "密碼已更改" in changed.text
    stored = path.read_text(encoding="utf-8")
    assert "abcdef" not in stored
    assert verify_password(stored, "abcdef")
    assert not verify_password(stored, PASSWORD)

    guest = TestClient(client.app)
    assert guest.post("/login", data={"password": PASSWORD}, follow_redirects=False).status_code == 401
    assert guest.post("/login", data={"password": "abcdef"}, follow_redirects=False).status_code == 303
    assert guest.get("/password").status_code == 200


def test_forwarder_posts_bearer_and_retries(tmp_path: Path) -> None:
    store = Store(tmp_path / "events.db")
    event_id = "a" * 64
    jpeg = b"\xff\xd8small"
    huge = b"\xff\xd8" + b"x" * 20
    store.add_event(
        event_id=event_id,
        received_at="2026-10-06T04:00:00+00:00",
        source_ip="10.1.0.11",
        content_type="multipart/form-data",
        event_type="VMD",
        channel="1",
        event_time="2026-10-06T12:00:00+08:00",
        event_xml="<EventNotificationAlert></EventNotificationAlert>",
        images=[
            {"filename": "small.jpg", "content_type": "image/jpeg", "size": len(jpeg), "data": jpeg},
            {"filename": "big.jpg", "content_type": "image/jpeg", "size": len(huge), "data": huge},
        ],
    )
    settings = Settings(
        data_dir=tmp_path,
        database_path=tmp_path / "events.db",
        cf_ingest_url="https://example.test/ingest",
        cf_ingest_token="token",
        max_image_bytes=len(jpeg),
        max_attempts=2,
        run_forwarder=False,
    )
    seen: dict[str, object] = {}

    def ok(request: httpx.Request) -> httpx.Response:
        assert request.headers["Authorization"] == "Bearer token"
        seen["body"] = json.loads(request.content.decode())
        return httpx.Response(200, json={"ok": True})

    async def succeed() -> None:
        async with httpx.AsyncClient(transport=httpx.MockTransport(ok)) as http:
            assert await forward_once(store, http, settings) is True

    asyncio.run(succeed())
    body = seen["body"]
    assert isinstance(body, dict)
    assert body["id"] == event_id
    assert body["source_ip"] == "10.1.0.11"
    assert {item["filename"]: "base64" in item for item in body["images"]} == {
        "small.jpg": True,
        "big.jpg": False,
    }
    assert store.get_event(event_id)["status"] == "sent"
    assert store.get_image(event_id, store.list_images(event_id)[1]["id"])["data"] == huge

    store.mark_failure(event_id, "reset", utc_now(), max_attempts=99)
    store.retry_event(event_id)
    calls = {"n": 0}

    def fail(_request: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        return httpx.Response(503, text="busy")

    moment = datetime(2026, 1, 1, tzinfo=timezone.utc)

    async def retry() -> None:
        async with httpx.AsyncClient(transport=httpx.MockTransport(fail)) as http:
            assert await forward_once(store, http, settings, now=moment) is True
            assert await forward_once(store, http, settings, now=moment) is False
            later = moment + timedelta(seconds=5)
            assert await forward_once(store, http, settings, now=later) is True

    asyncio.run(retry())
    assert calls["n"] == 2
    failed = store.get_event(event_id)
    assert failed["status"] == "failed"
    assert "HTTP 503" in failed["last_error"]


def test_forwarder_leaves_events_when_url_missing(tmp_path: Path) -> None:
    store = Store(tmp_path / "events.db")
    store.add_event(
        event_id="b" * 64,
        received_at="2026-10-06T04:00:00+00:00",
        source_ip="10.0.0.1",
        content_type="application/xml",
        event_type="VMD",
        channel="1",
        event_time="2026-10-06T12:00:00+08:00",
        event_xml="<a/>",
        images=[],
    )
    settings = Settings(
        data_dir=tmp_path,
        database_path=tmp_path / "events.db",
        run_forwarder=False,
    )

    async def once() -> bool:
        async with httpx.AsyncClient() as http:
            return await forward_once(store, http, settings)

    assert asyncio.run(once()) is False
    assert store.get_event("b" * 64)["status"] == "pending"


def test_restart_returns_sending_events_to_pending(tmp_path: Path) -> None:
    store = Store(tmp_path / "events.db")
    store.add_event(
        event_id="c" * 64,
        received_at="2026-10-06T04:00:00+00:00",
        source_ip="10.0.0.1",
        content_type="application/xml",
        event_type="",
        channel="",
        event_time="",
        event_xml="",
        images=[],
    )
    assert store.claim_pending(utc_now()) is not None
    assert store.get_event("c" * 64)["status"] == "sending"
    store.recover_sending()
    assert store.get_event("c" * 64)["status"] == "pending"


def test_multipart_parser_reads_hik_parts() -> None:
    body = (
        b"--bound\r\n"
        b'Content-Disposition: form-data; name="event_log"\r\n'
        b"Content-Type: application/xml\r\n"
        b"\r\n"
        + xml().encode()
        + b"\r\n"
        b"--bound\r\n"
        b'Content-Disposition: form-data; name="Picture"; filename="event.jpg"\r\n'
        b"Content-Type: image/jpeg\r\n"
        b"\r\n"
        b"\xff\xd8\xff\xd9\r\n"
        b"\r\n"
        b"--bound--\r\n"
    )
    parsed = parse_body("multipart/form-data; boundary=bound", body)
    assert parsed.event_type == "VMD"
    assert parsed.images[0].filename == "event.jpg"
    assert parsed.images[0].data == b"\xff\xd8\xff\xd9\r\n"


def test_oversized_image_omits_base64_but_keeps_bytes() -> None:
    payload = build_payload(
        {
            "id": "d" * 64,
            "received_at": "2026-10-06T04:00:00+00:00",
            "source_ip": "10.0.0.8",
            "content_type": "multipart/form-data",
            "event_xml": "<a/>",
        },
        [{"filename": "big.jpg", "content_type": "image/jpeg", "size": 8, "data": b"12345678"}],
        max_image_bytes=4,
    )
    assert "base64" not in payload["images"][0]
    assert payload["images"][0]["size"] == 8


def test_compose_publishes_port_9000_and_worker_is_reference() -> None:
    compose = (WEBHOOK / "docker-compose.yml").read_text(encoding="utf-8")
    assert "9000:9000" in compose
    dockerfile = (WEBHOOK / "Dockerfile").read_text(encoding="utf-8")
    assert "9000" in dockerfile
    entry = (WEBHOOK / "entrypoint.sh").read_text(encoding="utf-8")
    assert "app.passwords" in entry
    assert "--port 9000" in entry
    worker = (WEBHOOK / "cloudflare" / "worker.js").read_text(encoding="utf-8")
    assert "Bearer" in worker
    assert not (WEBHOOK / "wrangler.toml").exists()
    readme = (WEBHOOK / "README.md").read_text(encoding="utf-8")
    assert "9000/hik/events" in readme
    project = (ROOT / "README.md").read_text(encoding="utf-8")
    assert "9000/hik/events" in project
