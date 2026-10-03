"""框架可預期的錯誤。"""


class ConfigError(Exception):
    """清單、設定檔或參數內容不正確。"""


class TransportError(Exception):
    """連線失敗、逾時，或 HTTP 層無法完成請求。"""


class TemplateError(Exception):
    """設定模板裡的變數無法代換。"""


class XmlPathError(Exception):
    """ISAPI XML 裡找不到路徑，或路徑不能寫入。"""
