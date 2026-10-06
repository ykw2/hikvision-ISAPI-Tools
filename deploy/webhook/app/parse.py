"""拆海康 HTTP 監聽的 XML 與 multipart。"""

from __future__ import annotations

import hashlib
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field


@dataclass
class ImagePart:
    filename: str
    content_type: str
    data: bytes


@dataclass
class ParsedEvent:
    event_xml: str = ""
    event_type: str = ""
    channel: str = ""
    event_time: str = ""
    images: list[ImagePart] = field(default_factory=list)


@dataclass
class BodyPart:
    name: str
    filename: str
    content_type: str
    data: bytes


def event_id(event_time: str, channel: str, event_type: str, source_ip: str, body: bytes) -> str:
    """同一時間、通道、類型與來源重送時得到同一個 id。三個欄位都空才把本文算進去。"""
    base = f"{event_time}|{channel}|{event_type}|{source_ip}"
    if not event_time and not channel and not event_type:
        base = f"{base}|{hashlib.sha256(body).hexdigest()}"
    return hashlib.sha256(base.encode("utf-8")).hexdigest()


def parse_body(content_type: str, body: bytes) -> ParsedEvent:
    if "multipart/" in content_type.lower():
        return _from_parts(parse_multipart(content_type, body))
    text = decode_text(body, content_type)
    if _looks_like_image(content_type, "", body):
        return ParsedEvent(images=[ImagePart(filename=_image_name(content_type, ""), content_type=_image_type(content_type, ""), data=body)])
    parsed = ParsedEvent(event_xml=text)
    _fill_xml_fields(parsed, text)
    return parsed


def decode_text(data: bytes, content_type: str = "") -> str:
    encodings: list[str] = []
    charset = re.search(r"charset\s*=\s*\"?([^\"\s;]+)", content_type, re.I)
    if charset:
        encodings.append(charset.group(1))
    declared = re.search(br"encoding\s*=\s*[\"']([^\"']+)[\"']", data[:120])
    if declared:
        encodings.append(declared.group(1).decode("ascii", "ignore"))
    encodings.extend(["utf-8", "gb18030"])
    seen: set[str] = set()
    for encoding in encodings:
        key = encoding.lower()
        if key in seen:
            continue
        seen.add(key)
        try:
            return data.decode(encoding)
        except (LookupError, UnicodeDecodeError):
            continue
    return data.decode("utf-8", "replace")


def parse_multipart(content_type: str, body: bytes) -> list[BodyPart]:
    match = re.search(r"""boundary\s*=\s*"?([^";]+)""", content_type, re.I)
    if not match:
        return []
    boundary = match.group(1).strip().encode("utf-8", "surrogateescape")
    marker = b"--" + boundary
    parts: list[BodyPart] = []
    for chunk in body.split(marker):
        if not chunk or chunk in (b"--", b"--\r\n", b"--\n"):
            continue
        chunk = chunk.removeprefix(b"\r\n").removeprefix(b"\n")
        if chunk.startswith(b"--"):
            continue
        if chunk.endswith(b"\r\n"):
            chunk = chunk[:-2]
        elif chunk.endswith(b"\n"):
            chunk = chunk[:-1]
        header_blob, separator, data = chunk.partition(b"\r\n\r\n")
        if not separator:
            header_blob, separator, data = chunk.partition(b"\n\n")
        if not separator:
            continue
        headers = _header_map(header_blob)
        disposition = headers.get("content-disposition", "")
        filename = _disposition_param(disposition, "filename")
        name = _disposition_param(disposition, "name")
        part_type = headers.get("content-type", "").split(";", 1)[0].strip().lower()
        parts.append(BodyPart(name=name, filename=filename, content_type=part_type, data=data))
    return parts


def _from_parts(parts: list[BodyPart]) -> ParsedEvent:
    parsed = ParsedEvent()
    for part in parts:
        if _looks_like_image(part.content_type, part.filename, part.data):
            parsed.images.append(
                ImagePart(
                    filename=part.filename or _image_name(part.content_type, part.name),
                    content_type=_image_type(part.content_type, part.filename),
                    data=part.data,
                )
            )
            continue
        text = decode_text(part.data, part.content_type)
        if parsed.event_xml:
            continue
        if "xml" in part.content_type or text.lstrip().startswith("<") or "event" in part.name.lower():
            parsed.event_xml = text
            _fill_xml_fields(parsed, text)
    return parsed


def _fill_xml_fields(parsed: ParsedEvent, text: str) -> None:
    fields = xml_fields(text)
    parsed.event_type = fields.get("eventType", "")
    parsed.channel = fields.get("channelID") or fields.get("channel") or ""
    parsed.event_time = fields.get("dateTime", "")


def xml_fields(text: str) -> dict[str, str]:
    stripped = text.strip()
    if not stripped.startswith("<"):
        return {}
    try:
        root = ET.fromstring(stripped)
    except ET.ParseError:
        return {}
    found: dict[str, str] = {}
    wanted = {"eventType", "channelID", "channel", "dateTime"}
    for element in root.iter():
        name = element.tag.rsplit("}", 1)[-1]
        if name in wanted and name not in found and element.text and element.text.strip():
            found[name] = element.text.strip()
    return found


def _looks_like_image(content_type: str, filename: str, data: bytes) -> bool:
    lowered = content_type.lower()
    if lowered.startswith("image/"):
        return True
    name = filename.lower()
    if name.endswith((".jpg", ".jpeg", ".png", ".gif", ".webp")):
        return True
    return data.startswith(b"\xff\xd8") or data.startswith(b"\x89PNG")


def _image_type(content_type: str, filename: str) -> str:
    if content_type.lower().startswith("image/"):
        return content_type.split(";", 1)[0].strip().lower()
    name = filename.lower()
    if name.endswith(".png"):
        return "image/png"
    if name.endswith(".gif"):
        return "image/gif"
    if name.endswith(".webp"):
        return "image/webp"
    return "image/jpeg"


def _image_name(content_type: str, name: str) -> str:
    if name:
        return name
    if "png" in content_type:
        return "event.png"
    return "event.jpg"


def _header_map(header_blob: bytes) -> dict[str, str]:
    text = header_blob.decode("utf-8", "replace")
    headers: dict[str, str] = {}
    for line in text.splitlines():
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        headers[key.strip().lower()] = value.strip()
    return headers


def _disposition_param(disposition: str, key: str) -> str:
    match = re.search(rf"""{key}\s*=\s*"([^"]*)"|{key}\s*=\s*([^;\s]+)""", disposition, re.I)
    if not match:
        return ""
    return match.group(1) if match.group(1) is not None else match.group(2)
