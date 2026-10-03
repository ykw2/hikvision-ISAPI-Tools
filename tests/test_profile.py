from pathlib import Path

import pytest

from hik_isapi.errors import ConfigError
from hik_isapi.profile import load_profile

ROOT = Path(__file__).resolve().parents[1]


def test_standard_profile_loads() -> None:
    profile = load_profile(ROOT / "examples" / "profiles" / "standard.yaml")
    assert profile.name == "standard"
    assert [step.id for step in profile.steps] == [
        "probe",
        "timezone",
        "ntp",
        "osd",
        "verify_time",
        "verify_ntp",
    ]
    assert profile.vars["timezone"] == "CST-8:00:00"
    assert profile.steps[1].set_values[0] == ("timeMode", "NTP")
    assert profile.steps[2].set_values[2] == ("portNo", "123")
    assert profile.max_failure_ratio == 0.2
    assert profile.steps[0].on_error == "abort_camera"
    assert profile.steps[3].set_values[-1] == ("channelNameOverlay/channelName", "{{camera.name}}")


def test_body_file_is_loaded_relative_to_profile() -> None:
    profile = load_profile(ROOT / "examples" / "profiles" / "ntp-replace.yaml")
    body = profile.steps[1].body or ""
    assert "{{vars.ntp_server}}" in body
    assert profile.steps[1].method == "PUT"


def test_profile_rejects_unknown_fields_and_bad_steps(tmp_path: Path) -> None:
    path = tmp_path / "bad.yaml"
    path.write_text("name: bad\nsteps:\n  - id: one\n    mode: merge_xml\n    path: /ISAPI/System/time\n", encoding="utf-8")
    with pytest.raises(ConfigError, match="set 或 remove"):
        load_profile(path)

    path.write_text("name: bad\nextra: 1\nsteps:\n  - id: one\n    path: /ISAPI/System/deviceInfo\n", encoding="utf-8")
    with pytest.raises(ConfigError, match="未知欄位"):
        load_profile(path)

    path.write_text(
        "name: bad\nsteps:\n  - id: one\n    path: ISAPI/System/deviceInfo\n",
        encoding="utf-8",
    )
    with pytest.raises(ConfigError, match="path"):
        load_profile(path)
