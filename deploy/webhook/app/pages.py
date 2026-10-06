"""管理頁 HTML。樣式在 static/style.css，不載入外部圖檔。"""

from __future__ import annotations

import html

STATUS_LABEL = {
    "pending": "待送",
    "sending": "待送",
    "sent": "已送出",
    "failed": "失敗",
}


def esc(value: object) -> str:
    return html.escape("" if value is None else str(value), quote=True)


def login_page(error: str = "") -> str:
    message = f'<p class="error">{esc(error)}</p>' if error else ""
    body = f"""
    <section class="card narrow">
      <h1>登入</h1>
      <p class="meta">帳號是 admin。初始密碼印在第一次啟動的記錄裡。</p>
      {message}
      <form method="post" action="/login" class="stack">
        <label for="password">密碼</label>
        <input id="password" name="password" type="password" autocomplete="current-password" required>
        <button class="primary" type="submit">登入</button>
      </form>
    </section>
    """
    return layout("登入", body, csrf=None)


def list_page(events: list[dict], *, status: str, csrf: str, notice: str = "", truncated: bool = False) -> str:
    filters = _filters(status)
    banner = '<p class="ok">密碼已更改</p>' if notice == "password" else ""
    if not events:
        rows = '<p class="meta">還沒有收到事件。</p>'
    else:
        rendered = [_event_row(event) for event in events]
        note = '<p class="meta">只顯示最新 200 筆。</p>' if truncated else ""
        rows = f"""
        {note}
        <div class="board" role="table">
          <div class="row head" role="row">
            <span>時間</span><span>來源</span><span>事件類型</span><span>通道</span><span>狀態</span><span>嘗試</span>
          </div>
          {''.join(rendered)}
        </div>
        """
    body = f"""
    <section class="card">
      {banner}
      <div class="filters">{filters}</div>
      {rows}
    </section>
    """
    return layout("事件管理", body, csrf=csrf)


def detail_page(event: dict, images: list[dict], *, csrf: str, error: str = "") -> str:
    badge = _badge(event["status"])
    xml = event.get("event_xml") or ""
    truncated = ""
    if len(xml) > 100_000:
        xml = xml[:100_000]
        truncated = '<p class="meta">XML 只顯示前段。</p>'
    xml_block = f"<pre class=\"xml\">{esc(xml)}</pre>{truncated}" if xml else '<p class="meta">這筆沒有 XML。</p>'
    if images:
        shots = []
        for image in images:
            shots.append(
                f"""
                <figure>
                  <img class="shot" alt="{esc(image["filename"])}" src="/events/{esc(event["id"])}/images/{int(image["id"])}">
                  <figcaption class="meta">{esc(image["filename"])} · {int(image["size"])} bytes</figcaption>
                </figure>
                """
            )
        gallery = "".join(shots)
    else:
        gallery = '<p class="meta">沒有圖片。</p>'
    error_line = f'<p class="error">{esc(error)}</p>' if error else ""
    last_error = ""
    if event.get("last_error"):
        last_error = f'<div><dt>上次錯誤</dt><dd>{esc(event["last_error"])}</dd></div>'
    actions = ""
    if event["status"] in {"pending", "failed"}:
        actions += f"""
        <form method="post" action="/events/{esc(event["id"])}/retry">
          <input type="hidden" name="csrf" value="{esc(csrf)}">
          <button class="primary" type="submit">重送</button>
        </form>
        """
    actions += f"""
    <form method="post" action="/events/{esc(event["id"])}/delete" onsubmit="return confirm('刪除這筆事件？');">
      <input type="hidden" name="csrf" value="{esc(csrf)}">
      <button class="secondary" type="submit">刪除</button>
    </form>
    """
    when = event.get("event_time") or event.get("received_at") or ""
    body = f"""
    <section class="card">
      <p><a href="/">返回列表</a></p>
      {error_line}
      <dl class="facts">
        <div><dt>狀態</dt><dd>{badge}</dd></div>
        <div><dt>時間</dt><dd>{esc(when)}</dd></div>
        <div><dt>收到</dt><dd>{esc(event.get("received_at"))}</dd></div>
        <div><dt>來源</dt><dd>{esc(event.get("source_ip"))}</dd></div>
        <div><dt>事件類型</dt><dd>{esc(event.get("event_type"))}</dd></div>
        <div><dt>通道</dt><dd>{esc(event.get("channel"))}</dd></div>
        <div><dt>嘗試</dt><dd>{esc(event.get("attempts"))}</dd></div>
        <div class="wide"><dt>編號</dt><dd class="mono">{esc(event.get("id"))}</dd></div>
        {last_error}
      </dl>
      <div class="actions">{actions}</div>
      <h2>XML</h2>
      {xml_block}
      <h2>圖片</h2>
      {gallery}
    </section>
    """
    return layout("事件明細", body, csrf=csrf)


def password_page(csrf: str, error: str = "") -> str:
    message = f'<p class="error">{esc(error)}</p>' if error else ""
    body = f"""
    <section class="card narrow">
      <h1>更改密碼</h1>
      <p class="meta">新密碼至少 6 字元。更改後舊密碼立即失效。</p>
      {message}
      <form method="post" action="/password" class="stack">
        <input type="hidden" name="csrf" value="{esc(csrf)}">
        <label for="current_password">目前密碼</label>
        <input id="current_password" name="current_password" type="password" autocomplete="current-password" required>
        <label for="new_password">新密碼</label>
        <input id="new_password" name="new_password" type="password" autocomplete="new-password" minlength="6" required>
        <button class="primary" type="submit">儲存</button>
      </form>
    </section>
    """
    return layout("更改密碼", body, csrf=csrf)


def message_page(title: str, text: str, *, csrf: str | None) -> str:
    body = f'<section class="card narrow"><h1>{esc(title)}</h1><p>{esc(text)}</p><p><a href="/">返回</a></p></section>'
    return layout(title, body, csrf=csrf)


def layout(title: str, body: str, *, csrf: str | None) -> str:
    if csrf:
        nav = f"""
        <nav>
          <a href="/">事件</a>
          <a href="/password">更改密碼</a>
          <form method="post" action="/logout">
            <input type="hidden" name="csrf" value="{esc(csrf)}">
            <button type="submit">登出</button>
          </form>
        </nav>
        """
    else:
        nav = ""
    return f"""<!DOCTYPE html>
<html lang="zh-Hant">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{esc(title)}</title>
  <link rel="stylesheet" href="/static/style.css">
</head>
<body>
  <header class="top">
    <div class="brand"><strong>事件管理</strong><small>海康上報</small></div>
    {nav}
  </header>
  <main class="wrap">
    {body}
  </main>
</body>
</html>
"""


def _filters(status: str) -> str:
    items = [("", "全部"), ("pending", "待送"), ("sent", "已送出"), ("failed", "失敗")]
    links = []
    for value, label in items:
        href = "/" if not value else f"/?status={value}"
        klass = "on" if status == value else ""
        links.append(f'<a class="{klass}" href="{href}">{label}</a>')
    return "".join(links)


def _event_row(event: dict) -> str:
    when = event.get("event_time") or event.get("received_at") or ""
    href = f"/events/{esc(event['id'])}"
    return f"""
    <a class="row" role="row" href="{href}">
      <span data-label="時間">{esc(when)}</span>
      <span data-label="來源">{esc(event.get("source_ip"))}</span>
      <span data-label="事件類型">{esc(event.get("event_type"))}</span>
      <span data-label="通道">{esc(event.get("channel"))}</span>
      <span data-label="狀態">{_badge(event["status"])}</span>
      <span data-label="嘗試">{esc(event.get("attempts"))}</span>
    </a>
    """


def _badge(status: str) -> str:
    kind = "pending" if status in {"pending", "sending"} else status
    if kind not in {"pending", "sent", "failed"}:
        kind = "failed"
    return f'<span class="badge {kind}">{esc(STATUS_LABEL.get(status, status))}</span>'
