from pathlib import Path

import pytest

from hik_isapi.errors import ConfigError
from hik_isapi.inventory import inventory_warnings, load_inventory, select_cameras

ROOT = Path(__file__).resolve().parents[1]


def test_example_inventory_and_filters() -> None:
    cameras = load_inventory(ROOT / "examples" / "inventory.csv")
    assert [camera.id for camera in cameras] == ["cam-0001", "cam-0002", "cam-0003"]
    assert cameras[0].name == "大門"
    assert cameras[0].attributes["site"] == "A棟"
    assert cameras[0].tags == ("gate", "north")
    assert cameras[2].use_https is True
    assert cameras[2].enabled is False

    selected = select_cameras(cameras, tags=["parking", "gate"])
    assert [camera.id for camera in selected] == ["cam-0001", "cam-0002"]
    assert select_cameras(cameras, only={"cam-0002"})[0].id == "cam-0002"
    assert select_cameras(cameras, limit=1)[0].id == "cam-0001"


def test_large_inventory_selection(tmp_path: Path) -> None:
    lines = ["id,host,name,enabled,tags"]
    for index in range(2000):
        enabled = "false" if index == 3 else "true"
        tag = "north" if index % 2 == 0 else "south"
        lines.append(f"cam-{index:04d},10.{index // 65536}.{(index // 256) % 256}.{index % 256},鏡頭{index},{enabled},{tag}")
    path = tmp_path / "cameras.csv"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    cameras = load_inventory(path)
    assert len(cameras) == 2000
    selected = select_cameras(cameras, tags=["north"], offset=5, limit=10)
    assert len(selected) == 10
    assert all("north" in camera.tags for camera in selected)
    assert selected[0].id != "cam-0000"


def test_inventory_rejects_bad_rows(tmp_path: Path) -> None:
    path = tmp_path / "bad.csv"
    path.write_text("id,host\ncam 1,10.0.0.1\n", encoding="utf-8")
    with pytest.raises(ConfigError, match="空白"):
        load_inventory(path)

    path.write_text("id,host\ncam-1,10.0.0.1:80\n", encoding="utf-8")
    with pytest.raises(ConfigError, match="port"):
        load_inventory(path)

    path.write_text("id,host\ncam-1,10.0.0.1\ncam-1,10.0.0.2\n", encoding="utf-8")
    with pytest.raises(ConfigError, match="重複"):
        load_inventory(path)

    path.write_text("name,host\n大門,10.0.0.1\n", encoding="utf-8")
    with pytest.raises(ConfigError, match="id 與 host"):
        load_inventory(path)


def test_disabled_only_and_duplicate_host(tmp_path: Path) -> None:
    path = tmp_path / "cams.csv"
    path.write_text(
        "id,host,port,enabled\ncam-1,10.0.0.1,80,true\ncam-2,10.0.0.1,80,false\n",
        encoding="utf-8",
    )
    cameras = load_inventory(path)
    assert inventory_warnings(cameras)[0].startswith("cam-2")
    with pytest.raises(ConfigError, match="已停用"):
        select_cameras(cameras, only={"cam-2"})
    with pytest.raises(ConfigError, match="沒有這些 id"):
        select_cameras(cameras, only={"missing"})
