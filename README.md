# hik-isapi

把同一套設定套到大批海康威視攝影機。你維護兩份資料：

- **清單**：每支攝影機的位址、帳號、密碼
- **設定檔**：要下達的 ISAPI 步驟

然後用同一個設定檔分批、並發、可重跑地套用。探測失敗或設定檔寫錯時，失敗率保護會停掉剩下的攝影機，避免 2000 支一起套用錯誤內容。

## 安裝

需要 Python 3.11 以上。

```bash
python -m pip install -e ".[dev]"
```

Windows 也可以用視窗操作，見下方「Windows 視窗」。

檢查清單與設定檔（不會連線）：

```bash
hik-isapi validate \
  --inventory examples/inventory.csv \
  --profile examples/profiles/standard.yaml
```

## Windows 視窗

`windows/HikIsapi.sln` 是 WinForms 程式，在 Windows 上操作同一套指令。它不會另外實作 ISAPI，而是啟動已安裝的 `hik-isapi`，所以演練、合併 XML、重試與失敗率保護的行為跟命令列相同。

需要：

- Windows
- Python 3.11 以上，並在專案目錄執行 `py -3 -m pip install -e .`
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（只執行已發布的程式時，改裝 .NET 8 Desktop Runtime）

```powershell
dotnet build windows\HikIsapi.sln -c Release
dotnet run --project windows\HikIsapi.Win\HikIsapi.Win.csproj -c Release
```

視窗可以選清單、設定檔、標籤、數量上限、並發與失敗率。按鈕對應 `validate`、`probe`、`apply --dry-run` 與正式 `apply`。正式套用與重跑失敗會先詢問，預設按鈕是取消。數量上限空白代表全部符合條件的攝影機。

密碼打在視窗裡時，只放進這次子行程的環境變數，不會出現在命令列，也不會寫進 `%AppData%\hik-isapi\settings.json`。清單某一列自己有密碼時，仍以那一列為準。

報告寫到工作目錄下的 `results`。工作目錄空白時，使用清單所在的資料夾。

## 建議流程

1. 準備自己的清單，不要直接拿範例去連真的攝影機。
2. `validate` 確認清單與設定檔讀得起來。
3. `probe` 確認每一支都登入得了，並記下型號與韌體。
4. `get` 讀一台的 XML，確認節點名稱跟設定檔一致。
5. `apply --dry-run --limit 5` 看會改哪些欄位。演練仍會送出 GET，不會送出 PUT。
6. `apply --limit 5` 真正寫入五支，到畫面確認。
7. 拿掉 `--limit` 跑全量。
8. 若有失敗，用 `--retry-failed` 只重跑失敗的那幾支。

同型號、同韌體時，一份設定檔最合適。機型混雜時用清單的 `tags` 拆開，各寫一份設定檔。

## 清單

CSV 至少要有 `id` 與 `host`。範例見 `examples/inventory.csv`。

| 欄位 | 說明 |
| --- | --- |
| id | 唯一代號，報告與 `--only` 都用它 |
| host | IP 或主機名。埠請寫在 `port`，不要寫成 `10.0.0.1:80` |
| port | 可空白。HTTP 預設 80，HTTPS 預設 443 |
| username | 可空白，改用設定檔或 `--username`，預設 `admin` |
| password | 可空白。空白時使用 `--password`，再空白才讀環境變數 |
| https | `true` 或 `false`。空白代表沿用設定檔 |
| name | 顯示名稱，可在設定檔用 `{{camera.name}}` |
| enabled | `false` 的攝影機不會被執行 |
| tags | 用分號或直線分隔，例如 `gate;north` |
| 其他欄 | 變成 `{{camera.欄位名}}`，例如 `site` |

2000 支若密碼相同，password 留空，執行前設定環境變數：

```bash
export HIK_PASSWORD='你的密碼'
```

密碼優先順序是：該列的 password、命令列 `--password`、環境變數。某一列有自己的密碼時，不會被共用密碼蓋掉。

正式清單請放在專案根目錄的 `inventory.csv` 或 `cameras.csv`。這兩個檔名已從 git 排除。

## 設定檔

一份設定檔就是依序執行的步驟。三種寫法：

- `request`：原樣送出 GET、PUT、POST 或 DELETE。`body` 或 `body_file` 可帶 XML。
- `merge_xml`：先 GET，只改你點名的節點，再 PUT 回去。沒有差異就不會寫入。
- `assert_xml`：GET 之後核對欄位，用來確認前面的設定真的寫進去。

`examples/profiles/standard.yaml` 會做這些事：

- 讀裝置資訊，失敗就不再動這支攝影機
- 校時模式改為 NTP，時區設為 `CST-8:00:00`（海康的 POSIX 格式，這是 UTC+8）
- 設定第一組 NTP
- OSD 改為 `YYYY-MM-DD`、24 小時制，通道名稱用清單裡的 `name`
- 回讀時間與 NTP

節點路徑用本地名稱，不寫命名空間。同一層有多個同名節點時，用 `NTPServer[1]` 表示第 2 個，從 0 開始。

```yaml
- id: ntp
  mode: merge_xml
  path: /ISAPI/System/time/ntpServers/1
  on_error: continue
  set:
    addressingFormatType: ipaddress
    ipAddress: "{{vars.ntp_server}}"
    portNo: 123
```

`on_error` 預設是 `abort_camera`：這一步失敗後，同一支的後續步驟略過。設成 `continue` 可以讓報告一次收集所有失敗步驟。`on_missing` 預設是 `error`：XML 裡沒有那個節點就失敗，不會自己發明節點。確定要補節點時才設 `create`。

整份覆蓋用 `request`。`examples/profiles/ntp-replace.yaml` 會把 `bodies/ntp-server.xml` 整份 PUT 上去。這種寫法會清掉文件裡沒寫到的欄位，只適合你已核對過的機型。

模板可以寫：

- `{{camera.name}}`、`{{camera.host}}`、`{{camera.id}}`，以及其他 CSV 欄位
- `{{vars.ntp_server}}`：設定檔 `vars` 裡的值
- `{{env.SOME_TOKEN}}`：環境變數

路徑也可以帶模板，例如 `/ISAPI/Streaming/channels/{{camera.channel}}`。模板不能讀取密碼。

## 命令

```bash
hik-isapi probe \
  --inventory inventory.csv \
  --profile examples/profiles/standard.yaml

hik-isapi get \
  --host 10.1.0.11 \
  --path /ISAPI/System/time \
  --output time.xml

hik-isapi apply \
  --inventory inventory.csv \
  --profile examples/profiles/standard.yaml \
  --dry-run \
  --limit 5

hik-isapi apply \
  --inventory inventory.csv \
  --profile examples/profiles/standard.yaml

hik-isapi apply \
  --inventory inventory.csv \
  --profile examples/profiles/standard.yaml \
  --retry-failed results/apply-20261003T100000Z.json
```

常用篩選：

- `--tag gate`：標籤符合其中一個就跑，可重複
- `--only cam-0001,cam-0002`
- `--limit`、`--offset`：先小批
- `--concurrency`：同時處理幾支，建議 20 到 40
- `--timeout`、`--retries`
- `--max-failure-ratio 1`：關閉失敗率保護
- `--quiet`

每次 `apply` 與 `probe` 會在 `results/` 寫一份 JSON，以及一份給試算表開啟的 CSV。CSV 使用 UTF-8 BOM。JSON 裡的 `changes` 會列出每個欄位改前與改後的值。

失敗率保護預設是：這次至少要跑 20 支才會計算；完成 20 支之後失敗率達到 20%，就不再開始新的攝影機。已經在跑的那一批會做完。樣本數小於門檻時不會觸發，所以 `--limit 5` 的試跑不會被中途打斷。

## 在程式裡呼叫

```python
from hik_isapi import load_inventory, load_profile, select_cameras, Runner

profile = load_profile("examples/profiles/standard.yaml")
cameras = select_cameras(load_inventory("inventory.csv"), limit=20)
report = Runner(profile).run(cameras, dry_run=True)
json_path, csv_path = report.write("results/dry-run.json")
print(report.success_count, report.failed_count)
```

`run()` 的 `client_factory` 可以換成自己的連線物件，只要提供 `request(method, path, content=None, headers=None)` 與 `close()`。

## 常見路徑

| 用途 | 路徑 |
| --- | --- |
| 裝置資訊 | `/ISAPI/System/deviceInfo` |
| 時間 | `/ISAPI/System/time` |
| 第一組 NTP | `/ISAPI/System/time/ntpServers/1` |
| OSD | `/ISAPI/System/Video/inputs/channels/1/overlays` |
| 主碼流 | `/ISAPI/Streaming/channels/101` |
| 子碼流 | `/ISAPI/Streaming/channels/102` |
| 影像參數 | `/ISAPI/Image/channels/1` |

這些路徑會因韌體不同而有差異。先對一台 `get`，再把實際看得到的節點寫進 `set`。

主碼流的 `maxFrameRate` 是每秒張數乘以 100，25fps 要填 `2500`。註解範例在 `examples/profiles/standard.yaml`。

## 連線與寫入行為

- 認證使用 HTTP Digest。若一直得到 HTTP 401，請核對帳號密碼，並確認攝影機的 Web 認證方式是 digest。
- 工具不會讀取 `HTTP_PROXY` 之類的環境變數，避免內網位址被送去代理。
- HTTPS 預設不驗證憑證，因為攝影機多半是自簽憑證。設定檔把 `verify_tls` 設為 `true` 才會驗證。
- `merge_xml` 會把 GET 回來的整份文件改完再 PUT。若裝置回 `Invalid XML Content`，代表裡面含有不能回寫的欄位。改打較小的子路徑、用 `remove` 拿掉那個節點，或改成 `request` 送一份只含可寫欄位的 XML。
- 回應要求重新開機時，該支仍算成功，報告的 `reboot_required` 會是 true。
- 連線逾時、HTTP 503 與 ISAPI 裝置忙碌會依 `retries` 重試。認證失敗與 XML 內容錯誤不會重試。

## 測試

```bash
pytest
```
