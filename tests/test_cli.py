from pathlib import Path

import pytest

from hik_isapi.cli import main

ROOT = Path(__file__).resolve().parents[1]
INVENTORY = ROOT / "examples" / "inventory.csv"
PROFILE = ROOT / "examples" / "profiles" / "standard.yaml"


def test_help_exits_cleanly() -> None:
    with pytest.raises(SystemExit) as caught:
        main(["--help"])
    assert caught.value.code == 0


def test_validate_example_inventory(capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["validate", "--inventory", str(INVENTORY), "--profile", str(PROFILE), "--limit", "1"])
    captured = capsys.readouterr()
    assert code == 0
    assert "未連線，只檢查清單與設定檔" in captured.out
    assert "符合條件 1 支" in captured.err
    assert "停用 1 支" in captured.err
    assert "verify_ntp" in captured.err
    assert "失敗率保護" in captured.err


def test_validate_missing_profile_is_a_config_error(capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["validate", "--inventory", str(INVENTORY), "--profile", str(ROOT / "missing.yaml")])
    captured = capsys.readouterr()
    assert code == 2
    assert "設定錯誤" in captured.err


def test_validate_reports_missing_password(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    inventory = tmp_path / "cameras.csv"
    inventory.write_text("id,host,name\ncam-1,10.0.0.8,大門\n", encoding="utf-8")
    code = main(["validate", "--inventory", str(inventory), "--profile", str(PROFILE)])
    captured = capsys.readouterr()
    assert code == 2
    assert "cam-1" in captured.err
    assert "HIK_PASSWORD" in captured.err
