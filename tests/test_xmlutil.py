from xml.etree import ElementTree as ET

import pytest

from hik_isapi.errors import XmlPathError
from hik_isapi.xmlutil import edit_xml, extract_device_info, find_node, parse_xml, serialize

NS = "http://www.hikvision.com/ver20/XMLSchema"

TIME_XML = f"""<?xml version="1.0" encoding="UTF-8"?>
<Time version="2.0" xmlns="{NS}">
  <timeMode>manual</timeMode>
  <timeZone>CST-8:00:00</timeZone>
</Time>
"""

NTP_LIST = f"""<?xml version="1.0" encoding="UTF-8"?>
<NTPServerList version="2.0" xmlns="{NS}">
  <NTPServer><id>1</id><ipAddress>1.1.1.1</ipAddress></NTPServer>
  <NTPServer><id>2</id><ipAddress>2.2.2.2</ipAddress></NTPServer>
</NTPServerList>
"""


def test_set_preserves_namespace_and_unrelated_fields() -> None:
    root = parse_xml(TIME_XML)
    edit = edit_xml(root, set_values=[("timeMode", "NTP")], remove_paths=[], on_missing="error")
    assert edit.changes == [{"action": "set", "path": "timeMode", "before": "manual", "after": "NTP"}]
    payload = serialize(root)
    assert b"ns0" not in payload
    assert f'xmlns="{NS}"'.encode() in payload
    assert b'version="2.0"' in payload
    again = parse_xml(payload)
    assert find_node(again, "timeMode", create=False).text == "NTP"
    assert find_node(again, "Time/timeZone", create=False).text == "CST-8:00:00"


def test_identical_value_is_not_a_change() -> None:
    root = parse_xml(TIME_XML)
    edit = edit_xml(root, set_values=[("timeZone", "CST-8:00:00")], remove_paths=[], on_missing="error")
    assert edit.changes == []


def test_indexed_path_updates_only_that_node() -> None:
    root = parse_xml(NTP_LIST)
    edit_xml(root, set_values=[("NTPServer[1]/ipAddress", "9.9.9.9")], remove_paths=[], on_missing="error")
    assert find_node(root, "NTPServer[0]/ipAddress", create=False).text == "1.1.1.1"
    assert find_node(root, "NTPServer[1]/ipAddress", create=False).text == "9.9.9.9"


def test_missing_node_and_parent_assignment() -> None:
    root = parse_xml(TIME_XML)
    with pytest.raises(XmlPathError, match="找不到節點"):
        edit_xml(root, set_values=[("noSuch", "1")], remove_paths=[], on_missing="error")
    with pytest.raises(XmlPathError, match="子節點"):
        edit_xml(root, set_values=[("Time", "1")], remove_paths=[], on_missing="error")


def test_skip_and_create_and_remove() -> None:
    root = parse_xml(TIME_XML)
    skipped = edit_xml(root, set_values=[("noSuch", "1")], remove_paths=["missing"], on_missing="skip")
    assert skipped.changes == []
    assert len(skipped.warnings) == 2

    created = edit_xml(root, set_values=[("daylightSaving", "false")], remove_paths=[], on_missing="create")
    assert created.changes[0]["path"] == "daylightSaving"
    assert find_node(root, "daylightSaving", create=False).text == "false"

    removed = edit_xml(root, set_values=[], remove_paths=["timeMode"], on_missing="error")
    assert removed.changes[0]["action"] == "remove"
    with pytest.raises(XmlPathError):
        find_node(root, "timeMode", create=False)


def test_chinese_and_escaped_text_roundtrip() -> None:
    root = parse_xml(f'<VideoOverlay xmlns="{NS}"><channelName>old</channelName></VideoOverlay>')
    edit_xml(root, set_values=[("channelName", "大門 & 倉庫")], remove_paths=[], on_missing="error")
    payload = serialize(root)
    assert "大門".encode() in payload
    assert b"&amp;" in payload
    assert find_node(parse_xml(payload), "channelName", create=False).text == "大門 & 倉庫"


def test_bad_path_syntax() -> None:
    root = parse_xml(TIME_XML)
    with pytest.raises(XmlPathError, match="無法解析"):
        find_node(root, "time mode", create=False)


def test_extract_device_info() -> None:
    body = f"""<DeviceInfo xmlns="{NS}">
      <deviceName>大門</deviceName>
      <model>DS-2CD2347</model>
      <serialNumber>ABC123</serialNumber>
      <firmwareVersion>V5.7.15</firmwareVersion>
      <macAddress>aa:bb:cc:dd:ee:ff</macAddress>
    </DeviceInfo>"""
    assert extract_device_info(body)["model"] == "DS-2CD2347"
    assert extract_device_info(body)["device_name"] == "大門"
    assert extract_device_info("not xml") == {}
    assert ET.fromstring(serialize(parse_xml(body))) is not None
