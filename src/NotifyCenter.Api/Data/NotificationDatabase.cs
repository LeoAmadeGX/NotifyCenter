using Npgsql;

namespace NotifyCenter.Api.Data;

public sealed class NotificationDatabase(NpgsqlDataSource dataSource, ILogger<NotificationDatabase> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS notification_items (
                id uuid PRIMARY KEY,
                dedupe_key text NOT NULL UNIQUE,
                source_system text NOT NULL,
                event_type text NOT NULL,
                channel text NOT NULL,
                target text NOT NULL,
                title text NOT NULL,
                body text NOT NULL,
                scheduled_at_utc timestamptz NOT NULL,
                status text NOT NULL,
                metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
                last_error text NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(),
                sent_at_utc timestamptz NULL,
                canceled_at_utc timestamptz NULL
            );

            CREATE INDEX IF NOT EXISTS ix_notification_items_status_due
                ON notification_items (status, scheduled_at_utc);

            CREATE INDEX IF NOT EXISTS ix_notification_items_source_event
                ON notification_items (source_system, event_type);

            CREATE TABLE IF NOT EXISTS notification_attempts (
                id uuid PRIMARY KEY,
                notification_id uuid NOT NULL REFERENCES notification_items(id) ON DELETE CASCADE,
                attempted_at_utc timestamptz NOT NULL DEFAULT now(),
                status text NOT NULL,
                http_status integer NULL,
                response_body text NULL,
                error text NULL
            );

            CREATE INDEX IF NOT EXISTS ix_notification_attempts_notification_id
                ON notification_attempts (notification_id, attempted_at_utc DESC);
            """;

        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Notification database schema is ready");
    }
}
