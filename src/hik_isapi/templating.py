"""把 {{camera.欄位}}、{{vars.名稱}}、{{env.變數}} 代進設定文字。"""

from __future__ import annotations

import re
from collections.abc import Mapping

from hik_isapi.errors import TemplateError

_TOKEN = re.compile(r"\{\{\s*([A-Za-z_][\w]*)\.([A-Za-z_][\w]*)\s*\}\}")


def render(
    text: str,
    *,
    camera: Mapping[str, str],
    variables: Mapping[str, str],
    environ: Mapping[str, str],
) -> str:
    """代換模板。沒有雙大括號時原樣回傳。"""

    def replace(match: re.Match[str]) -> str:
        scope, key = match.group(1), match.group(2)
        if scope == "camera":
            if key == "password":
                raise TemplateError("模板不能讀取密碼")
            if key not in camera:
                raise TemplateError(f"攝影機沒有欄位 {key}")
            return camera[key]
        if scope == "vars":
            if key not in variables:
                raise TemplateError(f"找不到變數 vars.{key}")
            return variables[key]
        if scope == "env":
            value = environ.get(key, "")
            if value == "":
                raise TemplateError(f"環境變數 {key} 未設定")
            return value
        raise TemplateError(f"不支援的模板範圍 {scope}，請使用 camera、vars 或 env")

    return _TOKEN.sub(replace, text)
