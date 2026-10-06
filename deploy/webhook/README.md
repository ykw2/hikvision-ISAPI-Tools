# 接收海康上報

攝影機用 HTTP 監聽把警報 POST 到這台機器。服務先把事件寫進 SQLite 並回 200，背景再推到 Cloudflare。同一個 **9000** 埠可以開網頁看事件、重送或刪除。

這份服務不改現有的 ISAPI 批次設定。Worker 腳本只供參考，這裡不會部署到 Cloudflare。

## 啟動

在這個目錄：

```bash
docker compose up -d --build
docker compose logs
```

第一次啟動會印出一行：

```text
管理頁初始密碼：……
```

之後重啟不會再印。帳號是 `admin`。密碼檔在 volume 裡只存雜湊。

管理頁：`http://<主機區網 IP>:9000/`

登入後可以改密碼。新密碼至少 6 字元。改完舊密碼立即失效，新密碼不會寫進記錄。

若要重設密碼，會同時清掉已收下的事件：

```bash
docker compose down
docker volume rm webhook_webhook-data
docker compose up -d
docker compose logs
```

`down` 預設不會刪除 volume。上面的 `volume rm` 才會把資料清掉。volume 名稱若不同，用 `docker volume ls` 對一下。

## 海康怎麼填

在攝影機或 NVR 的網頁找 HTTP 監聽（報警主機、`httpHosts`）。填：

- 位址：這台 Docker 主機的區網 IP
- 埠：`9000`
- 路徑：`/hik/events`
- 協定：HTTP

完整網址是 `http://<主機區網 IP>:9000/hik/events`。

ISAPI 路徑是 `/ISAPI/Event/notification/httpHosts`。常見欄位：

- `addressingFormatType`：`ipaddress`
- `ipAddress`：Docker 主機的區網 IP
- `portNo`：`9000`
- `url`：`/hik/events`
- `protocolType`：`HTTP`

沒有啟用收件帳密時，海康端認證選不驗證。若要驗證，在海康填同一組帳密，並在 `.env` 設定 `HIK_BASIC_USER` 與 `HIK_BASIC_PASSWORD`，然後 `docker compose up -d`。

可以先用這段確認收件，不必等攝影機：

```bash
curl -s -X POST http://127.0.0.1:9000/hik/events \
  -H 'Content-Type: application/xml' \
  --data '<EventNotificationAlert><eventType>VMD</eventType><channelID>1</channelID><dateTime>2026-10-06T12:00:00+08:00</dateTime></EventNotificationAlert>'
```

網頁上會看到這筆。同一時間、通道、事件類型與來源再送一次，不會多出一列。

## 網頁

未登入只能看到登入頁。登入後可以：

- 依待送、已送出、失敗篩選
- 點進一列看 XML 與圖片
- 待送或失敗按重送
- 刪除

待送是黃底，已送出是紅底，失敗是深色底。

## 轉送到 Cloudflare

把 `.env.example` 複製成 `.env`，填上你自己的 Worker 網址與 token：

```bash
CF_INGEST_URL=https://你的worker.workers.dev/ingest
CF_INGEST_TOKEN=一組長隨機字串
```

Worker 端把同一組字串放在 `INGEST_TOKEN`。參考程式在 [cloudflare/worker.js](cloudflare/worker.js)。請自行部署，這個專案不會執行 `wrangler deploy`。

送出的 JSON 有 `id`、`received_at`、`source_ip`、`content_type`、`event_xml`、`images`。圖片小於 `MAX_IMAGE_BYTES`（預設 1MB）才附 `base64`。超過的只送檔名、類型與大小。網頁仍看得到存在本機的原圖。

`CF_INGEST_URL` 留空時，事件留在待送，不會外送。轉送失敗會退避重試，超過 8 次改為失敗。程序重啟後，送到一半的事件會回到待送。

下游請用 `id` 去重。
