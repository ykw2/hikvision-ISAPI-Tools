"""依節點名稱修改海康 ISAPI XML。

路徑使用本地名稱，忽略命名空間。例如時間文件裡的 timeMode 寫成
``timeMode``；OSD 的日期格式寫成 ``DateTimeOverlay/dateStyle``。
同一層有多個同名節點時，可用 ``NTPServer[1]`` 指定第 2 個（從 0 開始）。
路徑開頭如果重複寫了根節點名稱，也會自動略過。
"""

from __future__ import annotations

import copy
import re
from dataclasses import dataclass
from xml.etree import ElementTree as ET

from hik_isapi.errors import XmlPathError

_PART = re.compile(r"^(?P<name>[A-Za-z_][\w.\-]*)(?:\[(?P<index>\d+)\])?$")


@dataclass(frozen=True)
class PathPart:
    name: str
    index: int | None


@dataclass
class XmlEdit:
    changes: list[dict[str, str | None]]
    warnings: list[str]


def local_name(tag: str) -> str:
    if tag.startswith("{"):
        return tag.split("}", 1)[1]
    return tag


def parse_xml(data: str | bytes) -> ET.Element:
    if isinstance(data, str):
        payload = data.encode("utf-8")
    else:
        payload = data
    payload = payload.lstrip(b"\xef\xbb\xbf").strip()
    return ET.fromstring(payload)


def parse_path(path: str) -> list[PathPart]:
    raw = path.strip().strip("/")
    if not raw:
        raise XmlPathError("路徑是空的")
    parts: list[PathPart] = []
    for segment in raw.split("/"):
        piece = segment.strip()
        matched = _PART.fullmatch(piece)
        if not matched:
            raise XmlPathError(f"無法解析路徑「{path}」的節點「{piece}」")
        index_text = matched.group("index")
        parts.append(PathPart(matched.group("name"), int(index_text) if index_text is not None else None))
    return parts


def child_elements(parent: ET.Element, name: str) -> list[ET.Element]:
    return [child for child in list(parent) if local_name(child.tag) == name]


def direct_text(parent: ET.Element, name: str) -> str | None:
    matches = child_elements(parent, name)
    if not matches:
        return None
    text = matches[0].text
    return text.strip() if text else ""


def find_node(root: ET.Element, path: str, *, create: bool) -> ET.Element:
    parts = parse_path(path)
    if parts and parts[0].name == local_name(root.tag) and parts[0].index is None:
        parts = parts[1:]
    if not parts:
        return root
    node = root
    for part in parts:
        node = _descend(node, part, create=create, full_path=path)
    return node


def edit_xml(
    root: ET.Element,
    *,
    set_values: list[tuple[str, str]],
    remove_paths: list[str],
    on_missing: str,
) -> XmlEdit:
    """修改 root。on_missing 為 error、skip 或 create。"""
    if on_missing not in {"error", "skip", "create"}:
        raise XmlPathError(f"不支援的 on_missing：{on_missing}")

    changes: list[dict[str, str | None]] = []
    warnings: list[str] = []
    create = on_missing == "create"

    if on_missing == "error":
        located = [(path, value, find_node(root, path, create=False)) for path, value in set_values]
        for path, value, node in located:
            _assign(node, path, value, changes)
    else:
        for path, value in set_values:
            try:
                node = find_node(root, path, create=create)
            except XmlPathError:
                if on_missing == "skip":
                    warnings.append(f"找不到節點 {path}，已略過")
                    continue
                raise
            _assign(node, path, value, changes)

    for path in remove_paths:
        if _remove(root, path):
            changes.append({"action": "remove", "path": path, "before": None, "after": None})
        else:
            warnings.append(f"找不到要移除的節點 {path}")
    return XmlEdit(changes=changes, warnings=warnings)


def serialize(elem: ET.Element) -> bytes:
    """輸出 UTF-8 XML。預設命名空間維持在根節點，不會變成 ns0 前綴。"""
    clone = copy.deepcopy(elem)
    uris: list[str] = []

    def collect(node: ET.Element) -> None:
        tag = node.tag
        if isinstance(tag, str) and tag.startswith("{"):
            uri = tag[1:].split("}", 1)[0]
            if uri not in uris:
                uris.append(uri)
        for child in list(node):
            collect(child)

    def rewrite(node: ET.Element) -> None:
        tag = node.tag
        if isinstance(tag, str) and tag.startswith("{"):
            uri, name = tag[1:].split("}", 1)
            if uris and uri == uris[0]:
                node.tag = name
            else:
                node.tag = f"ns{uris.index(uri)}:{name}"
        for child in list(node):
            rewrite(child)

    collect(clone)
    if uris:
        rewrite(clone)
        clone.set("xmlns", uris[0])
        for index, uri in enumerate(uris):
            if index == 0:
                continue
            clone.set(f"xmlns:ns{index}", uri)
    payload = ET.tostring(clone, encoding="utf-8", xml_declaration=False)
    return b'<?xml version="1.0" encoding="UTF-8"?>\n' + payload


def extract_device_info(body: str) -> dict[str, str]:
    """從 /ISAPI/System/deviceInfo 取出常用欄位。解析失敗時回傳空對應。"""
    try:
        root = parse_xml(body)
    except ET.ParseError:
        return {}
    wanted = {
        "deviceName": "device_name",
        "model": "model",
        "serialNumber": "serial_number",
        "firmwareVersion": "firmware_version",
        "firmwareReleasedDate": "firmware_released",
        "macAddress": "mac_address",
        "deviceType": "device_type",
    }
    found: dict[str, str] = {}
    for child in list(root):
        name = local_name(child.tag)
        if name in wanted and child.text and child.text.strip():
            found[wanted[name]] = child.text.strip()
    return found


def _qualified(parent: ET.Element, name: str) -> str:
    if isinstance(parent.tag, str) and parent.tag.startswith("{"):
        uri = parent.tag[1:].split("}", 1)[0]
        return f"{{{uri}}}{name}"
    return name


def _descend(node: ET.Element, part: PathPart, *, create: bool, full_path: str) -> ET.Element:
    matches = child_elements(node, part.name)
    if part.index is None:
        if matches:
            return matches[0]
        if create:
            return ET.SubElement(node, _qualified(node, part.name))
        raise XmlPathError(f"找不到節點 {full_path}")
    if part.index < len(matches):
        return matches[part.index]
    if create and part.index == len(matches):
        return ET.SubElement(node, _qualified(node, part.name))
    if create:
        raise XmlPathError(f"無法建立節點 {full_path}，索引必須接在現有節點後面")
    raise XmlPathError(f"找不到節點 {full_path}")


def _assign(node: ET.Element, path: str, value: str, changes: list[dict[str, str | None]]) -> None:
    if any(isinstance(child.tag, str) for child in list(node)):
        raise XmlPathError(f"{path} 底下還有子節點，不能直接指定文字")
    after = value.strip()
    before = (node.text or "").strip()
    if before == after:
        return
    node.text = after
    changes.append({"action": "set", "path": path, "before": before, "after": after})


def _remove(root: ET.Element, path: str) -> bool:
    parts = parse_path(path)
    if parts and parts[0].name == local_name(root.tag) and parts[0].index is None:
        parts = parts[1:]
    if not parts:
        raise XmlPathError("不能移除根節點")
    parent = root
    if len(parts) > 1:
        parent_path = "/".join(_format_part(part) for part in parts[:-1])
        try:
            parent = find_node(root, parent_path, create=False)
        except XmlPathError:
            return False
    last = parts[-1]
    matches = child_elements(parent, last.name)
    if last.index is None:
        if not matches:
            return False
        parent.remove(matches[0])
        return True
    if last.index >= len(matches):
        return False
    parent.remove(matches[last.index])
    return True


def _format_part(part: PathPart) -> str:
    if part.index is None:
        return part.name
    return f"{part.name}[{part.index}]"
