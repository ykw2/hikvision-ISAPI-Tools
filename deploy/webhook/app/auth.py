"""管理頁的 session cookie。收件的 Basic Auth 與這組登入分開。"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import time
from typing import Any

COOKIE = "hik_session"
SESSION_SECONDS = 12 * 3600


def encode_session(payload: dict[str, Any], secret: bytes) -> str:
    raw = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    body = base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")
    signature = hmac.new(secret, body.encode("ascii"), hashlib.sha256).hexdigest()
    return f"{body}.{signature}"


def decode_session(token: str | None, secret: bytes) -> dict[str, Any] | None:
    if not token or "." not in token:
        return None
    body, signature = token.rsplit(".", 1)
    expected = hmac.new(secret, body.encode("ascii"), hashlib.sha256).hexdigest()
    if not hmac.compare_digest(expected, signature):
        return None
    padding = "=" * (-len(body) % 4)
    try:
        payload = json.loads(base64.urlsafe_b64decode(body + padding))
    except (ValueError, json.JSONDecodeError):
        return None
    if not isinstance(payload, dict) or payload.get("u") != "admin":
        return None
    try:
        expires = int(payload.get("exp", 0))
    except (TypeError, ValueError):
        return None
    if expires < int(time.time()):
        return None
    csrf = payload.get("csrf")
    if not isinstance(csrf, str) or not csrf:
        return None
    return payload


def new_session(secret: bytes, csrf: str) -> str:
    payload = {"u": "admin", "exp": int(time.time()) + SESSION_SECONDS, "csrf": csrf}
    return encode_session(payload, secret)


def csrf_matches(session: dict[str, Any], provided: str) -> bool:
    expected = session.get("csrf", "")
    if not isinstance(expected, str) or not provided:
        return False
    return hmac.compare_digest(expected, provided)
