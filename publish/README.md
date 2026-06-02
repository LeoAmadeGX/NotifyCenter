# NotifyCenter Publish Bundle

這個資料夾是給 NAS / Docker 主機直接部署用的自足交付包。

你要做的是：

1. 把 `publish` 內的所有檔案與資料夾，整包複製到 NAS 上的部署目錄
2. 在 NAS 的部署目錄中，複製 `.env.example` 為 `.env`
3. 填入 `POSTGRES_PASSWORD`、`JWT_SIGNING_KEY`、`TELEGRAM_BOT_TOKEN`、`LINE_CHANNEL_ACCESS_TOKEN`
4. 先執行 `sh ./preflight-check.sh`
5. 確認檢查通過後，再執行 `docker compose up -d --build`
6. 開啟 `http://localhost:17051` 進入管理台
7. 登入後到「對象管理」設定 Telegram / Line / Teams 的實際收件對象
8. 若要收集 LINE userId / groupId / roomId，請把管理台「對象管理」頁顯示的 webhook URL 設到 LINE Developers

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

補充：

- 管理台現在以實際 `delivery` 派送單為主視圖
- 主畫面會每 60 秒局部刷新清單與統計，且在背景派送結果回來時用 SSE 即時刷新
- 如果 producer 沒有提供 `target override`，實際派送對象會由 NotifyCenter 內建的 routing targets 決定
- `TELEGRAM_DEFAULT_CHAT_ID` 仍可保留作為相容設定，但不再是未指定 target 時的主要派送決策來源
- Telegram bot token 仍由 `.env` 的 `TELEGRAM_BOT_TOKEN` 全域提供；對象管理中的 Telegram `Destination` 只填 chat id，預設 parse mode 為 `HTML`
- LINE push message 會使用 `.env` 的 `LINE_CHANNEL_ACCESS_TOKEN`；`LINE_CHANNEL_SECRET` 可先留空只做來源收集，填入後 webhook 會啟用簽章驗證
- `preflight-check.sh` 會檢查 `.env`、Docker Compose 設定、`app-shared` network，以及這次容易漏掉的 API / Web 關鍵檔案
