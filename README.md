# NotifyCenter

NotifyCenter is a channel-agnostic notification scheduler and dispatcher.

Its job is to:

- receive generic notification requests
- validate shared JWTs
- deduplicate by deterministic `dedupeKey`
- schedule and dispatch notifications
- record delivery history

`GameDashboard` is only one producer. Any producer can send `title`, `body`,
`scheduledAtUtc`, `channel`, `target`, and producer-specific `metadata`.

## v1 channels

- `telegram`

The public model stays generic so future senders such as `line`, `teams`,
`webhook`, and `email` can be added without changing the core notification
contract.

## Local run

1. Copy `.env.example` to `.env`
2. Fill in `JWT_SIGNING_KEY` and `TELEGRAM_BOT_TOKEN`
3. Start with `docker compose up -d --build`

