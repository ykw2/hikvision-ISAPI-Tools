"""9000 埠同時收海康上報與提供管理頁。"""

from __future__ import annotations

import asyncio
import base64
import contextlib
import hmac
import logging
import re
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse, Response
from fastapi.staticfiles import StaticFiles

from app.auth import COOKIE, SESSION_SECONDS, csrf_matches, decode_session, new_session
from app.db import Store, iso, utc_now
from app.forwarder import forward_loop
from app.parse import event_id, parse_body
from app.passwords import (
    acceptable_new_password,
    ensure_gui_password,
    ensure_session_secret,
    read_password_hash,
    verify_password,
    write_password_hash,
)
from app.pages import detail_page, list_page, login_page, message_page, password_page
from app.settings import Settings

STATIC_DIR = Path(__file__).resolve().parent / "static"
MAX_BODY = 32 * 1024 * 1024
EVENT_ID = re.compile(r"^[0-9a-f]{64}$")
log = logging.getLogger("hik_webhook")


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings if settings is not None else Settings.from_env()
    settings.data_dir.mkdir(parents=True, exist_ok=True)
    _configure_log()
    ensure_gui_password(settings.password_path, print_initial=True)
    secret = ensure_session_secret(settings.session_secret_path)
    store = Store(settings.database_path)
    store.recover_sending()

    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI):
        stop = asyncio.Event()
        task: asyncio.Task | None = None
        if settings.run_forwarder:
            task = asyncio.create_task(forward_loop(store, settings, stop))
        try:
            yield
        finally:
            stop.set()
            if task is not None:
                task.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await task
            store.close()

    app = FastAPI(lifespan=lifespan)
    app.state.settings = settings
    app.state.store = store
    app.state.secret = secret
    app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

    @app.get("/favicon.ico")
    async def favicon() -> Response:
        return Response(status_code=204)

    @app.post("/hik/events")
    async def hik_events(request: Request) -> Response:
        if not _hik_authorized(request, settings):
            return Response(status_code=401, headers={"WWW-Authenticate": 'Basic realm="hik"'})
        body = await request.body()
        if len(body) > MAX_BODY:
            return Response("payload too large", status_code=413)
        content_type = request.headers.get("content-type", "")
        source_ip = request.client.host if request.client else ""
        parsed = parse_body(content_type, body)
        identifier = event_id(parsed.event_time, parsed.channel, parsed.event_type, source_ip, body)
        images = [
            {
                "filename": image.filename or "event.jpg",
                "content_type": image.content_type or "image/jpeg",
                "size": len(image.data),
                "data": image.data,
            }
            for image in parsed.images
        ]
        created = store.add_event(
            event_id=identifier,
            received_at=iso(utc_now()),
            source_ip=source_ip,
            content_type=content_type,
            event_type=parsed.event_type,
            channel=parsed.channel,
            event_time=parsed.event_time,
            event_xml=parsed.event_xml,
            images=images,
        )
        return JSONResponse({"id": identifier, "duplicate": not created})

    @app.get("/")
    async def home(request: Request) -> HTMLResponse:
        session = _session(request)
        if session is None:
            return _html(login_page())
        status = request.query_params.get("status", "")
        if status not in {"pending", "sent", "failed"}:
            status = ""
        events = store.list_events(status or None)
        notice = request.query_params.get("notice", "")
        if notice != "password":
            notice = ""
        page = list_page(
            events,
            status=status,
            csrf=session["csrf"],
            notice=notice,
            truncated=len(events) >= 200,
        )
        return _html(page)

    @app.post("/login")
    async def login(request: Request) -> Response:
        form = await _form(request)
        password = form.get("password", "")
        if not _password_matches(settings, password):
            return _html(login_page("帳號或密碼不正確"), status_code=401)
        token = new_session(secret, _new_csrf())
        response = RedirectResponse("/", status_code=303)
        response.set_cookie(COOKIE, token, httponly=True, samesite="lax", max_age=SESSION_SECONDS, path="/")
        return response

    @app.post("/logout")
    async def logout(request: Request) -> Response:
        session = _session(request)
        form = await _form(request)
        response = RedirectResponse("/", status_code=303)
        if session is not None and csrf_matches(session, form.get("csrf", "")):
            response.delete_cookie(COOKIE, path="/")
        return response

    @app.get("/password")
    async def password_form(request: Request) -> Response:
        session = _session(request)
        if session is None:
            return RedirectResponse("/", status_code=303)
        return _html(password_page(session["csrf"]))

    @app.post("/password")
    async def change_password(request: Request) -> Response:
        session = _session(request)
        if session is None:
            return RedirectResponse("/", status_code=303)
        form = await _form(request)
        if not csrf_matches(session, form.get("csrf", "")):
            return _html(message_page("無法更改", "這次送出沒有通過驗證，請再試一次。", csrf=session["csrf"]), 400)
        current = form.get("current_password", "")
        new_password = form.get("new_password", "")
        if not _password_matches(settings, current):
            return _html(password_page(session["csrf"], "目前密碼不正確"))
        if not acceptable_new_password(new_password):
            return _html(password_page(session["csrf"], "新密碼至少要 6 字元"))
        write_password_hash(settings.password_path, new_password.strip())
        return RedirectResponse("/?notice=password", status_code=303)

    @app.get("/events/{event_id}")
    async def event_detail(request: Request, event_id: str) -> Response:
        session = _session(request)
        if session is None:
            return RedirectResponse("/", status_code=303)
        event = _load_event(store, event_id)
        if event is None:
            return _html(message_page("找不到", "找不到這筆事件。", csrf=session["csrf"]), 404)
        images = store.list_images(event_id)
        return _html(detail_page(event, images, csrf=session["csrf"]))

    @app.post("/events/{event_id}/retry")
    async def retry_event(request: Request, event_id: str) -> Response:
        session = _session(request)
        if session is None:
            return RedirectResponse("/", status_code=303)
        form = await _form(request)
        if not csrf_matches(session, form.get("csrf", "")):
            return _html(message_page("無法重送", "這次送出沒有通過驗證，請再試一次。", csrf=session["csrf"]), 400)
        if _load_event(store, event_id) is None:
            return _html(message_page("找不到", "找不到這筆事件。", csrf=session["csrf"]), 404)
        if not store.retry_event(event_id):
            event = store.get_event(event_id)
            images = store.list_images(event_id)
            return _html(detail_page(event, images, csrf=session["csrf"], error="這筆目前不能重送。"))
        return RedirectResponse(f"/events/{event_id}", status_code=303)

    @app.post("/events/{event_id}/delete")
    async def delete_event(request: Request, event_id: str) -> Response:
        session = _session(request)
        if session is None:
            return RedirectResponse("/", status_code=303)
        form = await _form(request)
        if not csrf_matches(session, form.get("csrf", "")):
            return _html(message_page("無法刪除", "這次送出沒有通過驗證，請再試一次。", csrf=session["csrf"]), 400)
        if not EVENT_ID.fullmatch(event_id) or not store.delete_event(event_id):
            return _html(message_page("找不到", "找不到這筆事件。", csrf=session["csrf"]), 404)
        return RedirectResponse("/", status_code=303)

    @app.get("/events/{event_id}/images/{image_id}")
    async def event_image(request: Request, event_id: str, image_id: int) -> Response:
        if _session(request) is None:
            return Response(status_code=401)
        if not EVENT_ID.fullmatch(event_id):
            return Response(status_code=404)
        image = store.get_image(event_id, image_id)
        if image is None:
            return Response(status_code=404)
        media_type = image["content_type"] or "application/octet-stream"
        return Response(content=image["data"], media_type=media_type)

    return app


def _configure_log() -> None:
    if log.handlers:
        return
    handler = logging.StreamHandler()
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    log.addHandler(handler)
    log.setLevel(logging.INFO)
    log.propagate = False


def _session(request: Request) -> dict | None:
    return decode_session(request.cookies.get(COOKIE), request.app.state.secret)


def _html(content: str, status_code: int = 200) -> HTMLResponse:
    return HTMLResponse(content, status_code=status_code, headers={"Cache-Control": "no-store"})


async def _form(request: Request) -> dict[str, str]:
    raw = (await request.body()).decode("utf-8", "replace")
    parsed = _parse_qs(raw)
    return parsed


def _parse_qs(raw: str) -> dict[str, str]:
    from urllib.parse import parse_qs

    parsed = parse_qs(raw, keep_blank_values=True)
    return {key: values[0] if values else "" for key, values in parsed.items()}


def _new_csrf() -> str:
    import secrets

    return secrets.token_hex(16)


def _password_matches(settings: Settings, password: str) -> bool:
    if len(password.encode("utf-8")) > 1024:
        return False
    try:
        stored = read_password_hash(settings.password_path)
    except OSError:
        return False
    return verify_password(stored, password)


def _hik_authorized(request: Request, settings: Settings) -> bool:
    if not settings.hik_basic_user and not settings.hik_basic_password:
        return True
    header = request.headers.get("authorization", "")
    scheme, _, encoded = header.partition(" ")
    if scheme.lower() != "basic" or not encoded:
        return False
    try:
        decoded = base64.b64decode(encoded.strip(), validate=True).decode("utf-8")
    except (ValueError, UnicodeDecodeError):
        return False
    user, separator, password = decoded.partition(":")
    if not separator:
        return False
    return hmac.compare_digest(user, settings.hik_basic_user) and hmac.compare_digest(
        password, settings.hik_basic_password
    )


def _load_event(store: Store, event_id: str) -> dict | None:
    if not EVENT_ID.fullmatch(event_id):
        return None
    return store.get_event(event_id)
