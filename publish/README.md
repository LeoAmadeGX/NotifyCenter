# NotifyCenter Publish Bundle

這個資料夾是給 NAS / Docker 主機直接部署用的自足交付包。

你要做的是：

1. 把 `publish` 內的所有檔案與資料夾，整包複製到 NAS 上的部署目錄
2. 在 NAS 的部署目錄中，複製 `.env.example` 為 `.env`
3. 填入 `POSTGRES_PASSWORD`、`JWT_SIGNING_KEY`、`TELEGRAM_BOT_TOKEN`、`TELEGRAM_DEFAULT_CHAT_ID`
4. 直接在該目錄執行 `docker compose up -d --build`
5. 開啟 `http://localhost:17051` 進入管理台

這個 bundle 已經包含：

- `docker-compose.yml`
- `api.Dockerfile`
- `web.Dockerfile`
- `nginx.conf`
- `src/NotifyCenter.Api`
- `src/NotifyCenter.Web`

部署後：

- `web` 對外使用 `17051`
- `api` 對外使用 `17052`
- `db` 僅在容器網路內使用 `17053`

首次空資料庫啟動時會建立 bootstrap admin 帳號 `amadegx`。登入後請立刻到管理台修改密碼。
