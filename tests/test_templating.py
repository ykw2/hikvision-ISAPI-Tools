import pytest

from hik_isapi.errors import ConfigError, TemplateError
from hik_isapi.templating import render
from hik_isapi.values import xml_text


def test_render_camera_vars_and_env() -> None:
    text = render(
        "{{camera.name}} {{vars.ntp_server}} {{ env.SITE }}",
        camera={"name": "大門"},
        variables={"ntp_server": "10.1.1.10"},
        environ={"SITE": "A棟"},
    )
    assert text == "大門 10.1.1.10 A棟"


def test_render_errors() -> None:
    with pytest.raises(TemplateError, match="沒有欄位"):
        render("{{camera.site}}", camera={}, variables={}, environ={})
    with pytest.raises(TemplateError, match="密碼"):
        render("{{camera.password}}", camera={"password": "secret"}, variables={}, environ={})
    with pytest.raises(TemplateError, match="環境變數"):
        render("{{env.MISSING}}", camera={}, variables={}, environ={})
    with pytest.raises(TemplateError, match="vars.ntp"):
        render("{{vars.ntp}}", camera={}, variables={}, environ={})


def test_plain_text_is_unchanged() -> None:
    assert render("NTP", camera={}, variables={}, environ={}) == "NTP"


def test_xml_text_coercion() -> None:
    assert xml_text(True) == "true"
    assert xml_text(False) == "false"
    assert xml_text(60) == "60"
    assert xml_text(60.0) == "60"
    assert xml_text("{{camera.name}}") == "{{camera.name}}"
    with pytest.raises(ConfigError):
        xml_text(None)
