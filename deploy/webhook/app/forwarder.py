"""背景把待送事件 POST 到 Cloudflare。失敗會退避，超過次數改為失敗。"""

from __future__ import annotations

import asyncio
import base64
import json
import logging
from datetime import datetime

import httpx

from app.db import Store, utc_now
from app.settings import Settings

log = logging.getLogger("hik_webhook")


def build_payload(event: dict, images: list[dict], max_image_bytes: int) -> dict:
    encoded_images = []
    for image in images:
        item = {
            "filename": image["filename"],
            "content_type": image["content_type"],
            "size": image["size"],
        }
        data = image.get("data") or b""
        if image["size"] <= max_image_bytes:
            item["base64"] = base64.b64encode(data).decode("ascii")
        encoded_images.append(item)
    return {
        "id": event["id"],
        "received_at": event["received_at"],
        "source_ip": event["source_ip"],
        "content_type": event["content_type"],
        "event_xml": event["event_xml"],
        "images": encoded_images,
    }


async def forward_once(
    store: Store,
    client: httpx.AsyncClient,
    settings: Settings,
    *,
    now: datetime | None = None,
) -> bool:
    """有處理到一筆就回 True，佇列空或尚未到重試時間就回 False。"""
    if not settings.cf_ingest_url:
        return False
    moment = now or utc_now()
    event = store.claim_pending(moment)
    if event is None:
        return False
    images = store.list_images(event["id"], include_data=True)
    payload = build_payload(event, images, settings.max_image_bytes)
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    try:
        response = await client.post(
            settings.cf_ingest_url,
            content=body,
            headers={
                "Authorization": f"Bearer {settings.cf_ingest_token}",
                "Content-Type": "application/json; charset=utf-8",
            },
        )
        if response.status_code < 200 or response.status_code >= 300:
            detail = response.text[:300].replace("\n", " ")
            raise RuntimeError(f"HTTP {response.status_code}: {detail}")
    except Exception as exc:
        status = store.mark_failure(event["id"], str(exc), moment, settings.max_attempts)
        log.warning("轉送失敗 %s（%s）：%s", event["id"], status, exc)
        return True
    store.mark_sent(event["id"], moment)
    log.info("已轉送 %s", event["id"])
    return True


async def forward_loop(store: Store, settings: Settings, stop: asyncio.Event) -> None:
    announced = False
    async with httpx.AsyncClient(timeout=20.0) as client:
        while not stop.is_set():
            if not settings.cf_ingest_url:
                if not announced:
                    log.info("未設定 CF_INGEST_URL，事件留在待送")
                    announced = True
                await _wait(stop, 2)
                continue
            try:
                worked = await forward_once(store, client, settings)
            except Exception:
                log.exception("轉送迴圈發生未預期的錯誤")
                worked = False
            await _wait(stop, 0.2 if worked else 1)


async def _wait(stop: asyncio.Event, seconds: float) -> None:
    try:
        await asyncio.wait_for(stop.wait(), timeout=seconds)
    except TimeoutError:
        return
