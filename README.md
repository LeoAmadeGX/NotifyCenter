# NotifyCenter

NotifyCenter 是一個以 `.NET 10 + PostgreSQL + Vue 3` 建立的通知排程與派送中心。
目前正式支援的通知渠道只有 `Telegram`，但 API 契約保持通用，之後可以繼續擴充其他渠道。

## 目前功能 v1.0.0

- 接收單筆通知與批次通知請求
- 使用 JWT 驗證外部 producer 權限
- 依 `dedupeKey` 去重，避免重複建立
- 以 PostgreSQL 保存通知主檔與派送歷程
- 背景輪詢待送通知並自動派送
- 提供管理台查看通知狀態、明細、失敗紀錄、取消與重試
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
  `.NET 10` API、排程器、JWT 驗證、Telegram sender、資料庫初始化
- [src/NotifyCenter.Web](/H:/SiteProject/NotifyCenter/src/NotifyCenter.Web)
  `Vue 3 + Vite + TypeScript` 管理台
- [publish](/H:/SiteProject/NotifyCenter/publish)
  乾淨部署包，包含 compose、Dockerfile、Nginx 設定與部署版 env 範例

## Docker 部署

### 建議方式

1. 將 [publish](/H:/SiteProject/NotifyCenter/publish) 內的所有檔案完整複製到 NAS 或 Docker 主機上的部署目錄
2. 在部署目錄中複製 `.env.example` 成 `.env`
3. 填入必要 secret 與 Telegram 設定
4. 在該部署目錄執行 `docker compose up -d --build`
5. 開啟 `http://localhost:17051`

### 也可以從 repo root 啟動

1. 複製 [.env.example](/H:/SiteProject/NotifyCenter/.env.example) 成 `.env`
2. 填入必要 secret 與 Telegram 設定
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
  預設推送 chat id
  範例格式：`-1001234567890`
- `TELEGRAM_PARSE_MODE`
  預設 `Markdown`

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
TELEGRAM_PARSE_MODE=Markdown
```

## 管理 API

### Admin session

- `POST /api/admin/login`
- `POST /api/admin/logout`
- `GET /api/admin/session`
- `POST /api/admin/change-password`

### 通知管理

- `GET /api/notifications`
- `GET /api/notifications/stats`
- `GET /api/notifications/{id}`
- `GET /api/notifications/{id}/attempts`
- `POST /api/notifications/{id}/cancel`
- `POST /api/notifications/{id}/retry`

`GET /api/notifications` 支援查詢參數：

- `status`
- `channel`
- `sourceSystem`
- `eventType`
- `limit`

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

如果 `channel = telegram` 且 request 未提供 `target`，系統會回退使用 `TELEGRAM_DEFAULT_CHAT_ID`。

## 安全注意事項

- 不要把 `.env`、`publish/.env`、真實 token、真實 DB 密碼提交到 git
- `POSTGRES_PASSWORD` 是容器連資料庫要用的原始密碼，不能用雜湊代替；正確作法是只放在 runtime env 或 secret
- Admin 使用者密碼會做 salted hash，資料庫中不應出現明碼密碼欄位
- 若要交接 bootstrap 密碼，請用私下管道，不要寫進版本庫或長期文件

## 已知限制

- 目前正式通知渠道只有 `Telegram`
- 本倉庫尚未提供多管理員、忘記密碼、帳號改名功能
- 目前這台開發環境沒有 Docker CLI，且僅安裝 `.NET SDK 9.0.314`；若要實際 build / 跑整合測試，請在有 Docker 與 `.NET 10 SDK` 的環境執行


## 版本變更紀錄

- 初版上線，僅有 Telegram 機器人通知