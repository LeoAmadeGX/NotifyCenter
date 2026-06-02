# NotifyCenter

目前版本：v1.2.0

NotifyCenter 是一個以 `.NET 10 + PostgreSQL + Vue 3` 建立的通知排程與派送中心。
目前正式支援的通知渠道包含 `Telegram` 與 `LINE`，API 契約保持通用，之後可以繼續擴充其他渠道。

## 目前功能 v1.2.0

- 接收單筆通知與批次通知請求
- 使用 JWT 驗證外部 producer 權限
- 依 `dedupeKey` 去重，避免重複建立
- 以 PostgreSQL 保存通知需求主檔、實際派送單（delivery）與派送歷程
- 由 NotifyCenter 中心式管理各頻道收件對象（routing targets），建立通知後自動物化為獨立派送單
- 背景輪詢待送派送單，在實際送出前再次驗證 routing target 有效性
- 提供管理台查看派送狀態、明細、失敗紀錄、取消與重試
- 管理台支援 `messageQuery` 內容搜尋、進階篩選、對象管理與 SSE 局部自動刷新
- LINE webhook 來源收集器會紀錄 userId、groupId、roomId，管理台可再挑選加入 routing targets
- 管理台「對象管理」頁可直接顯示 LINE webhook URL、檢視來源清單，並一鍵建立 LINE routing target
- 首次空資料庫啟動時，自動建立 bootstrap admin 帳號 `amadegx`
- Admin 密碼只以 `PBKDF2-HMACSHA256 + salt` 雜湊保存，不會明碼寫入資料庫
- Admin 密碼變更後，舊的管理 session 會失效

## 架構與 Port

- `web`：Vue 管理台 + Nginx 反向代理，對外 `17051`
- `api`：NotifyCenter API，對外 `17052`
- `db`：PostgreSQL，只在容器內部使用 `17053`

瀏覽器只需要打 `http://localhost:17051`。
前端所有 `/api/*` 與 `/health` 都會由 `web` 反向代理到 `api`。

## 目錄

- [src/NotifyCenter.Api](/H:/SiteProject/NotifyCenter/src/NotifyCenter.Api)
  `.NET 10` API、排程器、JWT 驗證、Telegram / LINE sender、資料庫初始化
- [src/NotifyCenter.Web](/H:/SiteProject/NotifyCenter/src/NotifyCenter.Web)
  `Vue 3 + Vite + TypeScript` 管理台
- [publish](/H:/SiteProject/NotifyCenter/publish)
  乾淨部署包，包含 compose、Dockerfile、Nginx 設定與部署版 env 範例

## Docker 部署

### 建議方式

1. 將 [publish](/H:/SiteProject/NotifyCenter/publish) 內的所有檔案完整複製到 NAS 或 Docker 主機上的部署目錄
2. 在部署目錄中複製 `.env.example` 成 `.env`
3. 填入必要 secret 與 Telegram / LINE 設定
4. 在該部署目錄執行 `docker compose up -d --build`
5. 開啟 `http://localhost:17051`

### 也可以從 repo root 啟動

1. 複製 [.env.example](/H:/SiteProject/NotifyCenter/.env.example) 成 `.env`
2. 填入必要 secret 與 Telegram / LINE 設定
3. 在 repo root 執行 `docker compose up -d --build`

## Admin 管理台

- 首次空資料庫啟動時，系統會自動建立 bootstrap admin 帳號 `amadegx`
- bootstrap 密碼只用於第一次登入，且資料庫內只保存雜湊值
- 登入後管理台會持續提醒你修改密碼，直到完成為止
- 新密碼最少 8 字元

## 環境變數說明

### PostgreSQL / 系統密鑰

- `POSTGRES_DB`
  PostgreSQL 資料庫名稱，預設可用 `notify_center`
- `POSTGRES_USER`
  PostgreSQL 使用者名稱，預設可用 `notify`
- `POSTGRES_PASSWORD`
  PostgreSQL 密碼，請改成你自己的長密碼，不要提交到版本庫
- `JWT_SIGNING_KEY`
  API JWT 簽章密鑰，至少 32 字元，不能使用範例值
- `JWT_ISSUER`
  JWT 發行者，預設 `NotifyCenter`
- `JWT_AUDIENCE`
  JWT 受眾，預設 `NotifyCenterClients`
- `JWT_EXPIRES_MINUTES`
  外部 producer JWT 有效分鐘數，預設 `1440`
- `DATABASE_URL`
  可留空。使用 Docker Compose 時，系統會自動依 `POSTGRES_*` 組出容器內連線字串；只有在你要接外部資料庫時才需要自行覆寫
- `NOTIFICATION_POLL_SECONDS`
  派送器輪詢秒數，預設 `30`

### Telegram 渠道

- `TELEGRAM_BOT_TOKEN`
  Telegram Bot token
  範例格式：`123456789:replace-with-your-bot-token`
- `TELEGRAM_DEFAULT_CHAT_ID`
  舊版相容用的預設 chat id 參考值；目前實際派送對象建議改由 routing targets 管理
  範例格式：`-1001234567890`
- `TELEGRAM_PARSE_MODE`
  預設 `HTML`
  Telegram bot token 仍由 `.env` 的 `TELEGRAM_BOT_TOKEN` 全域提供；對象管理中的 Telegram `Destination` 只需要填 chat id

### LINE 渠道

- `LINE_CHANNEL_ACCESS_TOKEN`
  LINE Messaging API Channel access token，用於實際推送 LINE 訊息
- `LINE_CHANNEL_SECRET`
  LINE Messaging API Channel secret；可先留空只做來源收集，填入後 webhook 會驗證 `X-Line-Signature`
- Webhook URL
  將管理台「對象管理」頁顯示的 `POST /api/line/webhook` 完整網址設定到 LINE Developers。使用者、群組或聊天室與 bot 互動後，系統會紀錄對應的 `userId`、`groupId` 或 `roomId`，再由你決定是否加入派送對象。

### `.env.example` 範例重點

```env
POSTGRES_DB=notify_center
POSTGRES_USER=notify
POSTGRES_PASSWORD=change-this-postgres-password

ASPNETCORE_URLS=http://+:17052
DATABASE_URL=

JWT_ISSUER=NotifyCenter
JWT_AUDIENCE=NotifyCenterClients
JWT_SIGNING_KEY=replace-with-a-32-character-secret
JWT_EXPIRES_MINUTES=1440

NOTIFICATION_POLL_SECONDS=30

TELEGRAM_BOT_TOKEN=123456789:replace-with-your-bot-token
TELEGRAM_DEFAULT_CHAT_ID=-1001234567890
TELEGRAM_PARSE_MODE=HTML

LINE_CHANNEL_ACCESS_TOKEN=replace-with-your-line-channel-access-token
LINE_CHANNEL_SECRET=
```

## 管理 API

### Admin session

- `POST /api/admin/login`
- `POST /api/admin/logout`
- `GET /api/admin/session`
- `POST /api/admin/change-password`
- `GET /api/admin/events`

### 通知管理

- `GET /api/notifications`
- `GET /api/notifications/stats`
- `GET /api/notifications/{id}`
- `GET /api/notifications/{id}/attempts`
- `POST /api/notifications/{id}/cancel`
- `POST /api/notifications/{id}/retry`

`/api/notifications/{id}` 相關端點中的 `id` 現在指向的是實際 `delivery id`，不是原始通知需求 id。

`GET /api/notifications` 支援查詢參數：

- `status`
- `channel`
- `sourceSystem`
- `eventType`
- `messageQuery`
- `scheduledFromUtc`
- `scheduledToUtc`
- `limit`

### 對象管理

- `GET /api/routing-targets`
- `POST /api/routing-targets`
- `PATCH /api/routing-targets/{id}`
- `DELETE /api/routing-targets/{id}`
- `GET /api/line-sources`

### LINE webhook

- `POST /api/line/webhook`

## Producer API

外部系統仍然可以透過 Bearer JWT 呼叫：

- `POST /api/notifications`
- `POST /api/notifications/bulk`

Bearer token 需要相應 scope：

- `notifications.write`
- `notifications.read`
- `notifications.cancel`
- `notifications.retry`

Admin cookie session 也具備 `notifications.admin` 權限，因此管理台可直接操作同一組通知 API。

## 通知資料格式

單筆通知至少需要：

- `title`
- `body`
- `scheduledAtUtc`

可選欄位：

- `dedupeKey`
- `sourceSystem`
- `eventType`
- `channel`
- `target`
- `metadata`

`target` 現在代表單筆通知的 `target override`。如果 request 不提供 `target`，NotifyCenter 會依當下啟用中的 routing targets 決定要展開成哪些實際派送單。
因此，在派送前修改某個頻道的 routing targets，尚未送出的未來通知也會跟著改變實際派送對象。

## 安全注意事項

- 不要把 `.env`、`publish/.env`、真實 token、真實 DB 密碼提交到 git
- `POSTGRES_PASSWORD` 是容器連資料庫要用的原始密碼，不能用雜湊代替；正確作法是只放在 runtime env 或 secret
- Admin 使用者密碼會做 salted hash，資料庫中不應出現明碼密碼欄位
- 若要交接 bootstrap 密碼，請用私下管道，不要寫進版本庫或長期文件

## 已知限制

- 目前正式 sender 支援 `Telegram` 與 `LINE`
- `Teams` 這一輪仍只提供對象設定與管理介面，尚未實作實際 sender
- 本倉庫尚未提供多管理員、忘記密碼、帳號改名功能
- 若要跑完整整合驗證，仍需要可用的 PostgreSQL / Docker 環境

## 版本變更紀錄

### v1.2.0 — 2026-06-02

#### 渠道與 API

- 新增 LINE sender，支援 `channel: "line"` 以 routing targets 或 target override 推送文字訊息。
- 新增 `LINE_CHANNEL_ACCESS_TOKEN` 與 `LINE_CHANNEL_SECRET` 環境變數；當未設定 token 時，LINE sender 會明確拒絕送出，避免誤以為已成功派送。
- 新增 `POST /api/line/webhook` 收集 LINE user/group/room source，並以 `notification_line_sources` 保存。
- 新增 `GET /api/line-sources`，提供管理台讀取 LINE webhook 收集到的對象來源。

#### 管理台與操作流程

- 管理台「對象管理」由側邊面板調整為 full page，整合 routing targets 維護、LINE webhook URL 顯示與來源清單檢視。
- 可從 LINE webhook 收集到的 userId、groupId、roomId 一鍵建立 routing target，減少手動複製 destination 的操作成本。
- 通知列表與篩選介面同步補上 `line` 渠道選項，讓 LINE 派送資料可直接在既有查詢流程中檢視。

#### 部署與設定

- `.env.example`、`publish/.env.example`、`docker-compose.yml` 與 `publish/docker-compose.yml` 一併補上 LINE 所需設定，部署包可直接帶入新版環境變數。
- README 與部署說明同步更新為 `v1.2.0`，避免主倉庫與 `publish/` 部署包文件內容脫節。

### v1.1.0 — 2026-06-02

#### 架構調整

- **Delivery 物化模型**：每筆通知需求（`notification_items`）依啟用中的 routing targets 展開成獨立的派送單（`notification_deliveries`）；管理台與 API 現在以 delivery 為操作主體，而非原始通知需求。
- **傳送前二次驗證**：Dispatcher 在真正送出前，會再次確認派送單對應的 routing target 仍然有效；若 target 已停用或被刪除，該筆派送單將被標為 `skipped_no_target`。
- **新增資料表**：`notification_routing_targets`（routing target 主檔）、`notification_deliveries`（派送單）；`notification_attempts` 新增 `delivery_id` 欄位。
- **舊資料自動遷移**：首次啟動時，系統會自動將現有 `notification_items` 資料補建為對應的派送單，確保不中斷升級。

#### API 變更

- 新增 Routing Target 管理端點：`GET / POST /api/routing-targets`、`PATCH / DELETE /api/routing-targets/{id}`
- `POST /api/notifications` 與 `POST /api/notifications/bulk` 回應中新增 `deliveryId` 欄位
- `/api/notifications/{id}` 相關路徑的 `id` 改為指向 `delivery id`
- 新增派送狀態：`pending_no_target`（建立時無符合 target）、`skipped_no_target`（送出時 target 已失效）
- 通知統計新增 `pendingNoTarget`、`skipped` 計數

#### 管理台

- 列表改為以 delivery 為視角，顯示 `targetName`、是否為 target override、`skippedAtUtc` 等欄位
- 新增「對象管理」側邊面板，可直接在管理台新增、編輯、停用、刪除 routing targets（支援 Telegram / Line / Teams）
- 進階篩選新增 `messageQuery` 搜尋欄位、可展開／收合的進階篩選面板
- SSE 即時刷新：透過 `GET /api/admin/events` 接收 `deliveries_changed` 事件，自動刷新列表與統計，不需整頁輪詢

#### 其他

- 新增 `nginx.conf`（前端反向代理設定）、`favicon.svg`
- `publish/` 新增 `preflight-check.sh` 部署前環境檢查腳本

---

### v1.0.0

- 接收單筆與批次通知、JWT 驗證、`dedupeKey` 去重
- 管理台基本 delivery 視角、對象管理、進階篩選、SSE 局部刷新
