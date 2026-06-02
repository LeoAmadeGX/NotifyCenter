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
                delivery_id uuid NULL,
                attempted_at_utc timestamptz NOT NULL DEFAULT now(),
                status text NOT NULL,
                http_status integer NULL,
                response_body text NULL,
                error text NULL
            );

            CREATE INDEX IF NOT EXISTS ix_notification_attempts_notification_id
                ON notification_attempts (notification_id, attempted_at_utc DESC);

            ALTER TABLE notification_attempts
                ADD COLUMN IF NOT EXISTS delivery_id uuid NULL;

            CREATE TABLE IF NOT EXISTS notification_routing_targets (
                id uuid PRIMARY KEY,
                channel text NOT NULL,
                name text NOT NULL,
                destination text NOT NULL,
                is_enabled boolean NOT NULL DEFAULT TRUE,
                sort_order integer NOT NULL DEFAULT 0,
                metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_notification_routing_targets_channel_enabled
                ON notification_routing_targets (channel, is_enabled, sort_order, name);

            CREATE TABLE IF NOT EXISTS notification_deliveries (
                id uuid PRIMARY KEY,
                notification_id uuid NOT NULL REFERENCES notification_items(id) ON DELETE CASCADE,
                channel text NOT NULL,
                routing_target_id uuid NULL REFERENCES notification_routing_targets(id) ON DELETE SET NULL,
                target_name text NULL,
                resolved_target text NULL,
                is_target_override boolean NOT NULL DEFAULT FALSE,
                scheduled_at_utc timestamptz NOT NULL,
                status text NOT NULL,
                last_error text NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(),
                sent_at_utc timestamptz NULL,
                canceled_at_utc timestamptz NULL,
                skipped_at_utc timestamptz NULL
            );

            CREATE INDEX IF NOT EXISTS ix_notification_deliveries_status_due
                ON notification_deliveries (status, scheduled_at_utc);

            CREATE INDEX IF NOT EXISTS ix_notification_deliveries_notification_id
                ON notification_deliveries (notification_id, created_at);

            CREATE INDEX IF NOT EXISTS ix_notification_deliveries_channel_status
                ON notification_deliveries (channel, status, scheduled_at_utc);

            CREATE TABLE IF NOT EXISTS admin_users (
                id uuid PRIMARY KEY,
                username text NOT NULL UNIQUE,
                password_hash text NOT NULL,
                password_salt text NOT NULL,
                password_iterations integer NOT NULL,
                must_change_password boolean NOT NULL DEFAULT FALSE,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(),
                last_login_at timestamptz NULL
            );
            """;

        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Notification database schema is ready");
    }
}
