"""把 YAML 與 CSV 的值轉成 ISAPI 會接受的文字。"""

from __future__ import annotations

from hik_isapi.errors import ConfigError

_TRUE = {"1", "true", "yes", "y", "是"}
_FALSE = {"0", "false", "no", "n", "否"}


def xml_text(value: object) -> str:
    """把設定值轉成 XML 文字。布林值會變成 true / false。"""
    if value is None:
        raise ConfigError("設定值不能是 null")
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def parse_bool(value: object, label: str) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        lowered = value.strip().lower()
        if lowered in _TRUE:
            return True
        if lowered in _FALSE:
            return False
    raise ConfigError(f"{label} 必須是 true 或 false")


def as_int(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, (int, float, str)):
        raise ConfigError(f"{label} 必須是整數")
    try:
        number = int(str(value).strip())
    except ValueError as exc:
        raise ConfigError(f"{label} 必須是整數") from exc
    return number


def as_float(value: object, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float, str)):
        raise ConfigError(f"{label} 必須是數字")
    try:
        return float(str(value).strip())
    except ValueError as exc:
        raise ConfigError(f"{label} 必須是數字") from exc
