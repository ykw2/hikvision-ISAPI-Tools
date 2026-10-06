// 參考用的接收端。這個檔放在 repo 裡，不會從這裡執行 wrangler deploy。
// 下游如果會把事件存起來，請用 body.id 去重。攝影機重送時 id 不變。
export default {
  async fetch(request, env) {
    if (request.method !== "POST") {
      return new Response("method not allowed", { status: 405 });
    }
    const expected = env.INGEST_TOKEN || "";
    const header = request.headers.get("Authorization") || "";
    if (!expected || header !== `Bearer ${expected}`) {
      return new Response("unauthorized", { status: 401 });
    }
    let id = null;
    try {
      const event = await request.json();
      id = event && typeof event.id === "string" ? event.id : null;
    } catch (_error) {
      id = null;
    }
    return new Response(JSON.stringify({ ok: true, id }), {
      status: 200,
      headers: { "content-type": "application/json; charset=utf-8" },
    });
  },
};
