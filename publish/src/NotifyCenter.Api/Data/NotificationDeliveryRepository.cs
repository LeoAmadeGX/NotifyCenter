using Npgsql;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Data;

public sealed class NotificationDeliveryRepository(NpgsqlDataSource dataSource)
{
    private const string DeliveryColumns = """
        delivery.id,
        delivery.notification_id,
        notification.dedupe_key,
        notification.source_system,
        notification.event_type,
        delivery.channel,
        delivery.target_name,
        delivery.resolved_target,
        delivery.is_target_override,
        notification.title,
        notification.body,
        delivery.scheduled_at_utc,
        delivery.status,
        notification.metadata::text AS metadata_json,
        delivery.last_error,
        delivery.created_at,
        delivery.updated_at,
        delivery.sent_at_utc,
        delivery.canceled_at_utc,
        delivery.skipped_at_utc
        """;

    public async Task MigrateLegacyDataAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var legacyNotifications = await GetLegacyNotificationsWithoutDeliveriesAsync(connection, transaction, cancellationToken);
        foreach (var notification in legacyNotifications)
        {
            await InsertDeliveryAsync(
                connection,
                transaction,
                notification.Id,
                notification.Channel,
                null,
                string.IsNullOrWhiteSpace(notification.Target) ? null : "legacy override",
                string.IsNullOrWhiteSpace(notification.Target) ? null : notification.Target.Trim(),
                true,
                notification.ScheduledAtUtc,
                MapLegacyStatus(notification.Status, notification.Target),
                notification.LastError,
                notification.CreatedAt,
                notification.UpdatedAt,
                notification.SentAtUtc,
                notification.CanceledAtUtc,
                notification.Status == "skipped_no_target" ? notification.UpdatedAt : null,
                cancellationToken);
        }

        const string backfillAttemptsSql = """
            WITH attempt_delivery_map AS (
                SELECT
                    attempt.id AS attempt_id,
                    (
                        SELECT delivery.id
                        FROM notification_deliveries AS delivery
                        WHERE delivery.notification_id = attempt.notification_id
                        ORDER BY delivery.created_at, delivery.id
                        LIMIT 1
                    ) AS delivery_id
                FROM notification_attempts AS attempt
                WHERE attempt.delivery_id IS NULL
            )
            UPDATE notification_attempts AS attempt
            SET delivery_id = map.delivery_id
            FROM attempt_delivery_map AS map
            WHERE attempt.id = map.attempt_id
              AND map.delivery_id IS NOT NULL;
            """;

        await using (var command = CreateCommand(connection, transaction, backfillAttemptsSql))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Guid?> RematerializeForNotificationAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var notification = await LoadNotificationAsync(connection, transaction, notificationId, cancellationToken);
        if (notification is null)
        {
            return null;
        }

        const string deleteSql = """
            DELETE FROM notification_deliveries
            WHERE notification_id = @notification_id
              AND status IN ('pending', 'pending_no_target');
            """;

        await using (var deleteCommand = CreateCommand(connection, transaction, deleteSql))
        {
            deleteCommand.Parameters.AddWithValue("notification_id", notificationId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid? primaryDeliveryId;
        if (!string.IsNullOrWhiteSpace(notification.Target))
        {
            primaryDeliveryId = await InsertDeliveryAsync(
                connection,
                transaction,
                notification.Id,
                notification.Channel,
                null,
                "override",
                notification.Target.Trim(),
                true,
                notification.ScheduledAtUtc,
                "pending",
                null,
                null,
                null,
                null,
                null,
                null,
                cancellationToken);
        }
        else
        {
            var targets = await GetActiveRoutingTargetsAsync(
                connection,
                transaction,
                notification.Channel,
                cancellationToken);

            if (targets.Count == 0)
            {
                primaryDeliveryId = await InsertDeliveryAsync(
                    connection,
                    transaction,
                    notification.Id,
                    notification.Channel,
                    null,
                    null,
                    null,
                    false,
                    notification.ScheduledAtUtc,
                    "pending_no_target",
                    "No enabled routing targets are configured for this channel.",
                    null,
                    null,
                    null,
                    null,
                    null,
                    cancellationToken);
            }
            else
            {
                Guid? firstId = null;
                foreach (var target in targets)
                {
                    var deliveryId = await InsertDeliveryAsync(
                        connection,
                        transaction,
                        notification.Id,
                        notification.Channel,
                        target.Id,
                        target.Name,
                        target.Destination,
                        false,
                        notification.ScheduledAtUtc,
                        "pending",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        cancellationToken);

                    firstId ??= deliveryId;
                }

                primaryDeliveryId = firstId;
            }
        }

        await SetNotificationAggregateStateAsync(connection, transaction, notificationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return primaryDeliveryId;
    }

    public async Task RebuildScheduledDeliveriesForChannelAsync(string channel, CancellationToken cancellationToken)
    {
        var notificationIds = new List<Guid>();
        const string sql = """
            SELECT notification.id
            FROM notification_items AS notification
            WHERE notification.channel = @channel
              AND btrim(notification.target) = ''
              AND EXISTS (
                  SELECT 1
                  FROM notification_deliveries AS delivery
                  WHERE delivery.notification_id = notification.id
                    AND delivery.status IN ('pending', 'pending_no_target')
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM notification_attempts AS attempt
                  JOIN notification_deliveries AS delivery
                    ON delivery.id = attempt.delivery_id
                  WHERE delivery.notification_id = notification.id
              );
            """;

        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("channel", NormalizeChannel(channel));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                notificationIds.Add(reader.GetGuid(0));
            }
        }

        foreach (var notificationId in notificationIds)
        {
            await RematerializeForNotificationAsync(notificationId, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<NotificationItem>> ListAsync(
        NotificationListQuery query,
        CancellationToken cancellationToken)
    {
        var filters = new List<string>();
        await using var command = dataSource.CreateCommand(string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(query.Limit, 1, 500));

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            filters.Add("delivery.status = @status");
            command.Parameters.AddWithValue("status", query.Status.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Channel))
        {
            filters.Add("delivery.channel = @channel");
            command.Parameters.AddWithValue("channel", NormalizeChannel(query.Channel));
        }

        if (!string.IsNullOrWhiteSpace(query.SourceSystem))
        {
            filters.Add("notification.source_system = @source_system");
            command.Parameters.AddWithValue("source_system", query.SourceSystem.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            filters.Add("notification.event_type = @event_type");
            command.Parameters.AddWithValue("event_type", query.EventType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.MessageQuery))
        {
            filters.Add("(notification.title ILIKE @message_query OR notification.body ILIKE @message_query)");
            command.Parameters.AddWithValue("message_query", $"%{query.MessageQuery.Trim()}%");
        }

        if (query.ScheduledFromUtc.HasValue)
        {
            filters.Add("delivery.scheduled_at_utc >= @scheduled_from_utc");
            command.Parameters.AddWithValue("scheduled_from_utc", query.ScheduledFromUtc.Value.UtcDateTime);
        }

        if (query.ScheduledToUtc.HasValue)
        {
            filters.Add("delivery.scheduled_at_utc <= @scheduled_to_utc");
            command.Parameters.AddWithValue("scheduled_to_utc", query.ScheduledToUtc.Value.UtcDateTime);
        }

        var whereClause = filters.Count == 0
            ? string.Empty
            : $"\nWHERE {string.Join(" AND ", filters)}";

        command.CommandText = $"""
            SELECT {DeliveryColumns}
            FROM notification_deliveries AS delivery
            JOIN notification_items AS notification
              ON notification.id = delivery.notification_id{whereClause}
            ORDER BY delivery.scheduled_at_utc DESC, delivery.created_at DESC
            LIMIT @limit;
            """;

        return await ReadItemsAsync(command, cancellationToken);
    }

    public async Task<NotificationItem?> RefreshResolvedTargetAsync(
        Guid deliveryId,
        string missingTargetReason,
        CancellationToken cancellationToken)
    {
        var state = await LoadDispatchStateAsync(deliveryId, cancellationToken);
        if (state is null)
        {
            return null;
        }

        if (state.IsTargetOverride)
        {
            return await GetByIdAsync(deliveryId, cancellationToken);
        }

        if (!state.ActiveRoutingTargetExists || string.IsNullOrWhiteSpace(state.ActiveDestination))
        {
            await MarkSkippedAsync(
                deliveryId,
                state.NotificationId,
                missingTargetReason,
                cancellationToken);
            return await GetByIdAsync(deliveryId, cancellationToken);
        }

        const string sql = """
            UPDATE notification_deliveries
            SET target_name = @target_name,
                resolved_target = @resolved_target,
                updated_at = now()
            WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("target_name", (object?)state.ActiveTargetName ?? DBNull.Value);
        command.Parameters.AddWithValue("resolved_target", state.ActiveDestination);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetByIdAsync(deliveryId, cancellationToken);
    }

    public async Task<NotificationFilterOptionsResponse> GetFilterOptionsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(array_agg(DISTINCT channel ORDER BY channel), ARRAY[]::text[]) AS channels,
                COALESCE(array_agg(DISTINCT source_system ORDER BY source_system), ARRAY[]::text[]) AS source_systems,
                COALESCE(array_agg(DISTINCT event_type ORDER BY event_type), ARRAY[]::text[]) AS event_types
            FROM notification_items;
            """;

        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new NotificationFilterOptionsResponse([], [], []);
        }

        return new NotificationFilterOptionsResponse(
            reader.GetFieldValue<string[]>(reader.GetOrdinal("channels")),
            reader.GetFieldValue<string[]>(reader.GetOrdinal("source_systems")),
            reader.GetFieldValue<string[]>(reader.GetOrdinal("event_types")));
    }

    public async Task<NotificationStatsResponse> GetStatsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COUNT(*)::int AS total,
                COUNT(*) FILTER (WHERE status = 'pending')::int AS pending,
                COUNT(*) FILTER (WHERE status = 'pending_no_target')::int AS pending_no_target,
                COUNT(*) FILTER (WHERE status = 'sent')::int AS sent,
                COUNT(*) FILTER (WHERE status = 'failed')::int AS failed,
                COUNT(*) FILTER (WHERE status = 'canceled')::int AS canceled,
                COUNT(*) FILTER (WHERE status = 'skipped_no_target')::int AS skipped,
                COUNT(*) FILTER (
                    WHERE status IN ('pending', 'pending_no_target')
                      AND scheduled_at_utc <= now()
                )::int AS due
            FROM notification_deliveries;
            """;

        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new NotificationStatsResponse(0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new NotificationStatsResponse(
            reader.GetInt32(reader.GetOrdinal("total")),
            reader.GetInt32(reader.GetOrdinal("pending")),
            reader.GetInt32(reader.GetOrdinal("pending_no_target")),
            reader.GetInt32(reader.GetOrdinal("sent")),
            reader.GetInt32(reader.GetOrdinal("failed")),
            reader.GetInt32(reader.GetOrdinal("canceled")),
            reader.GetInt32(reader.GetOrdinal("skipped")),
            reader.GetInt32(reader.GetOrdinal("due")));
    }

    public async Task<NotificationItem?> GetByIdAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {DeliveryColumns}
            FROM notification_deliveries AS delivery
            JOIN notification_items AS notification
              ON notification.id = delivery.notification_id
            WHERE delivery.id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<IReadOnlyList<NotificationAttempt>> GetAttemptsAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, delivery_id, notification_id, attempted_at_utc, status, http_status, response_body, error
            FROM notification_attempts
            WHERE delivery_id = @delivery_id
            ORDER BY attempted_at_utc DESC;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("delivery_id", deliveryId);
        var attempts = new List<NotificationAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    public async Task<IReadOnlyList<NotificationItem>> GetDuePendingAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {DeliveryColumns}
            FROM notification_deliveries AS delivery
            JOIN notification_items AS notification
              ON notification.id = delivery.notification_id
            WHERE delivery.status IN ('pending', 'pending_no_target')
              AND delivery.scheduled_at_utc <= @now_utc
            ORDER BY delivery.scheduled_at_utc ASC, delivery.created_at ASC
            LIMIT @limit;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("now_utc", nowUtc.UtcDateTime);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        return await ReadItemsAsync(command, cancellationToken);
    }

    public async Task MarkSentAsync(
        NotificationItem delivery,
        int httpStatus,
        string responseBody,
        CancellationToken cancellationToken)
    {
        await AddAttemptAsync(delivery, "sent", httpStatus, responseBody, null, cancellationToken);

        const string sql = """
            UPDATE notification_deliveries
            SET status = 'sent',
                sent_at_utc = now(),
                last_error = NULL,
                updated_at = now()
            WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", delivery.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetNotificationAggregateStateAsync(delivery.NotificationId, cancellationToken);
    }

    public async Task MarkFailedAsync(
        NotificationItem delivery,
        int? httpStatus,
        string? responseBody,
        string error,
        CancellationToken cancellationToken)
    {
        await AddAttemptAsync(delivery, "failed", httpStatus, responseBody, error, cancellationToken);

        const string sql = """
            UPDATE notification_deliveries
            SET status = 'failed',
                last_error = @error,
                updated_at = now()
            WHERE id = @id
              AND status <> 'canceled';
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", delivery.Id);
        command.Parameters.AddWithValue("error", error);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetNotificationAggregateStateAsync(delivery.NotificationId, cancellationToken);
    }

    public async Task MarkSkippedNoTargetAsync(NotificationItem delivery, CancellationToken cancellationToken)
    {
        await MarkSkippedAsync(
            delivery.Id,
            delivery.NotificationId,
            "No enabled routing targets were configured when this delivery became due.",
            cancellationToken);
    }

    public async Task<NotificationItem?> CancelAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        Guid? notificationId;
        const string sql = """
            UPDATE notification_deliveries
            SET status = 'canceled',
                canceled_at_utc = now(),
                updated_at = now()
            WHERE id = @id
              AND status IN ('pending', 'pending_no_target', 'failed')
            RETURNING notification_id;
            """;

        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("id", deliveryId);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            notificationId = scalar is Guid guid ? guid : null;
        }

        if (!notificationId.HasValue)
        {
            return null;
        }

        await SetNotificationAggregateStateAsync(notificationId.Value, cancellationToken);
        return await GetByIdAsync(deliveryId, cancellationToken);
    }

    public async Task<NotificationItem?> RetryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var state = await LoadDispatchStateAsync(deliveryId, cancellationToken);
        if (state is null || !string.Equals(state.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!state.IsTargetOverride &&
            (!state.ActiveRoutingTargetExists || string.IsNullOrWhiteSpace(state.ActiveDestination)))
        {
            await MarkSkippedAsync(
                deliveryId,
                state.NotificationId,
                "This routing target is no longer enabled or available, so the failed delivery was skipped instead of retried.",
                cancellationToken);
            return await GetByIdAsync(deliveryId, cancellationToken);
        }

        const string sql = """
            UPDATE notification_deliveries
            SET status = 'pending',
                target_name = @target_name,
                resolved_target = @resolved_target,
                last_error = NULL,
                skipped_at_utc = NULL,
                sent_at_utc = NULL,
                canceled_at_utc = NULL,
                updated_at = now()
            WHERE id = @id
              AND status = 'failed';
            """;

        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("id", deliveryId);
            command.Parameters.AddWithValue("target_name", (object?)state.ActiveTargetName ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "resolved_target",
                (object?)(state.IsTargetOverride ? state.StoredTarget : state.ActiveDestination) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await SetNotificationAggregateStateAsync(state.NotificationId, cancellationToken);
        return await GetByIdAsync(deliveryId, cancellationToken);
    }

    private async Task<NotificationRow?> LoadNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, channel, target, scheduled_at_utc
            FROM notification_items
            WHERE id = @id;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("id", notificationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new NotificationRow(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("channel")),
            reader.GetString(reader.GetOrdinal("target")),
            ReadTimestamp(reader, "scheduled_at_utc")!.Value);
    }

    private async Task<IReadOnlyList<LegacyNotificationRow>> GetLegacyNotificationsWithoutDeliveriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                notification.id,
                notification.channel,
                notification.target,
                notification.scheduled_at_utc,
                notification.status,
                notification.last_error,
                notification.created_at,
                notification.updated_at,
                notification.sent_at_utc,
                notification.canceled_at_utc
            FROM notification_items AS notification
            WHERE NOT EXISTS (
                SELECT 1
                FROM notification_deliveries AS delivery
                WHERE delivery.notification_id = notification.id
            );
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        var items = new List<LegacyNotificationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LegacyNotificationRow(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("channel")),
                reader.GetString(reader.GetOrdinal("target")),
                ReadTimestamp(reader, "scheduled_at_utc")!.Value,
                reader.GetString(reader.GetOrdinal("status")),
                ReadNullableString(reader, "last_error"),
                ReadTimestamp(reader, "created_at"),
                ReadTimestamp(reader, "updated_at"),
                ReadTimestamp(reader, "sent_at_utc"),
                ReadTimestamp(reader, "canceled_at_utc")));
        }

        return items;
    }

    private async Task<DispatchStateRow?> LoadDispatchStateAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                delivery.id,
                delivery.notification_id,
                delivery.status,
                delivery.is_target_override,
                delivery.resolved_target,
                routing_target.id AS active_routing_target_id,
                routing_target.name AS active_target_name,
                routing_target.destination AS active_destination
            FROM notification_deliveries AS delivery
            LEFT JOIN notification_routing_targets AS routing_target
              ON routing_target.id = delivery.routing_target_id
             AND routing_target.is_enabled = TRUE
            WHERE delivery.id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DispatchStateRow(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("notification_id")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetBoolean(reader.GetOrdinal("is_target_override")),
            ReadNullableString(reader, "resolved_target"),
            !reader.IsDBNull(reader.GetOrdinal("active_routing_target_id")),
            ReadNullableString(reader, "active_target_name"),
            ReadNullableString(reader, "active_destination"));
    }

    private async Task<IReadOnlyList<RoutingTargetRow>> GetActiveRoutingTargetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string channel,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, destination
            FROM notification_routing_targets
            WHERE channel = @channel
              AND is_enabled = TRUE
            ORDER BY sort_order, name;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("channel", NormalizeChannel(channel));
        var items = new List<RoutingTargetRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RoutingTargetRow(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("destination"))));
        }

        return items;
    }

    private async Task<Guid> InsertDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid notificationId,
        string channel,
        Guid? routingTargetId,
        string? targetName,
        string? resolvedTarget,
        bool isTargetOverride,
        DateTimeOffset scheduledAtUtc,
        string status,
        string? lastError,
        DateTimeOffset? createdAt,
        DateTimeOffset? updatedAt,
        DateTimeOffset? sentAtUtc,
        DateTimeOffset? canceledAtUtc,
        DateTimeOffset? skippedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO notification_deliveries (
                id,
                notification_id,
                channel,
                routing_target_id,
                target_name,
                resolved_target,
                is_target_override,
                scheduled_at_utc,
                status,
                last_error,
                created_at,
                updated_at,
                sent_at_utc,
                canceled_at_utc,
                skipped_at_utc
            )
            VALUES (
                @id,
                @notification_id,
                @channel,
                @routing_target_id,
                @target_name,
                @resolved_target,
                @is_target_override,
                @scheduled_at_utc,
                @status,
                @last_error,
                COALESCE(@created_at, now()),
                COALESCE(@updated_at, now()),
                @sent_at_utc,
                @canceled_at_utc,
                @skipped_at_utc
            );
            """;

        var id = Guid.NewGuid();
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("notification_id", notificationId);
        command.Parameters.AddWithValue("channel", NormalizeChannel(channel));
        command.Parameters.AddWithValue("routing_target_id", (object?)routingTargetId ?? DBNull.Value);
        command.Parameters.AddWithValue("target_name", (object?)targetName ?? DBNull.Value);
        command.Parameters.AddWithValue("resolved_target", (object?)resolvedTarget ?? DBNull.Value);
        command.Parameters.AddWithValue("is_target_override", isTargetOverride);
        command.Parameters.AddWithValue("scheduled_at_utc", scheduledAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("last_error", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", (object?)createdAt?.UtcDateTime ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", (object?)updatedAt?.UtcDateTime ?? DBNull.Value);
        command.Parameters.AddWithValue("sent_at_utc", (object?)sentAtUtc?.UtcDateTime ?? DBNull.Value);
        command.Parameters.AddWithValue("canceled_at_utc", (object?)canceledAtUtc?.UtcDateTime ?? DBNull.Value);
        command.Parameters.AddWithValue("skipped_at_utc", (object?)skippedAtUtc?.UtcDateTime ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private async Task SetNotificationAggregateStateAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await SetNotificationAggregateStateAsync(connection, null, notificationId, cancellationToken);
    }

    private static async Task SetNotificationAggregateStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE notification_items
            SET status = CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM notification_deliveries
                        WHERE notification_id = @notification_id
                          AND status IN ('pending', 'pending_no_target')
                    ) THEN 'pending'
                    WHEN EXISTS (
                        SELECT 1
                        FROM notification_deliveries
                        WHERE notification_id = @notification_id
                          AND status = 'failed'
                    ) THEN 'failed'
                    WHEN EXISTS (
                        SELECT 1
                        FROM notification_deliveries
                        WHERE notification_id = @notification_id
                          AND status = 'sent'
                    ) THEN 'sent'
                    WHEN EXISTS (
                        SELECT 1
                        FROM notification_deliveries
                        WHERE notification_id = @notification_id
                          AND status = 'canceled'
                    ) THEN 'canceled'
                    WHEN EXISTS (
                        SELECT 1
                        FROM notification_deliveries
                        WHERE notification_id = @notification_id
                          AND status = 'skipped_no_target'
                    ) THEN 'canceled'
                    ELSE 'pending'
                END,
                last_error = (
                    SELECT delivery.last_error
                    FROM notification_deliveries AS delivery
                    WHERE delivery.notification_id = @notification_id
                      AND delivery.last_error IS NOT NULL
                    ORDER BY delivery.updated_at DESC
                    LIMIT 1
                ),
                sent_at_utc = (
                    SELECT MAX(delivery.sent_at_utc)
                    FROM notification_deliveries AS delivery
                    WHERE delivery.notification_id = @notification_id
                ),
                canceled_at_utc = (
                    SELECT MAX(delivery.canceled_at_utc)
                    FROM notification_deliveries AS delivery
                    WHERE delivery.notification_id = @notification_id
                ),
                updated_at = now()
            WHERE id = @notification_id;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("notification_id", notificationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkSkippedAsync(
        Guid deliveryId,
        Guid notificationId,
        string reason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE notification_deliveries
            SET status = 'skipped_no_target',
                skipped_at_utc = now(),
                last_error = @reason,
                updated_at = now()
            WHERE id = @id
              AND status IN ('pending', 'pending_no_target', 'failed');
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetNotificationAggregateStateAsync(notificationId, cancellationToken);
    }

    private async Task AddAttemptAsync(
        NotificationItem delivery,
        string status,
        int? httpStatus,
        string? responseBody,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO notification_attempts (
                id,
                notification_id,
                delivery_id,
                attempted_at_utc,
                status,
                http_status,
                response_body,
                error
            )
            VALUES (
                @id,
                @notification_id,
                @delivery_id,
                now(),
                @status,
                @http_status,
                @response_body,
                @error
            );
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("notification_id", delivery.NotificationId);
        command.Parameters.AddWithValue("delivery_id", delivery.Id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("http_status", (object?)httpStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("response_body", (object?)responseBody ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string MapLegacyStatus(string status, string target)
    {
        if (status == "pending" && string.IsNullOrWhiteSpace(target))
        {
            return "pending_no_target";
        }

        return status switch
        {
            "pending" => "pending",
            "sent" => "sent",
            "failed" => "failed",
            "canceled" => "canceled",
            _ => "pending"
        };
    }

    private static string NormalizeChannel(string channel)
    {
        return channel.Trim().ToLowerInvariant();
    }

    private static async Task<IReadOnlyList<NotificationItem>> ReadItemsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var items = new List<NotificationItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    private static NotificationItem ReadItem(NpgsqlDataReader reader)
    {
        return new NotificationItem(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("notification_id")),
            reader.GetString(reader.GetOrdinal("dedupe_key")),
            reader.GetString(reader.GetOrdinal("source_system")),
            reader.GetString(reader.GetOrdinal("event_type")),
            reader.GetString(reader.GetOrdinal("channel")),
            ReadNullableString(reader, "target_name"),
            ReadNullableString(reader, "resolved_target"),
            reader.GetBoolean(reader.GetOrdinal("is_target_override")),
            reader.GetString(reader.GetOrdinal("title")),
            reader.GetString(reader.GetOrdinal("body")),
            ReadTimestamp(reader, "scheduled_at_utc")!.Value,
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetString(reader.GetOrdinal("metadata_json")),
            ReadNullableString(reader, "last_error"),
            ReadTimestamp(reader, "created_at")!.Value,
            ReadTimestamp(reader, "updated_at")!.Value,
            ReadTimestamp(reader, "sent_at_utc"),
            ReadTimestamp(reader, "canceled_at_utc"),
            ReadTimestamp(reader, "skipped_at_utc"));
    }

    private static NotificationAttempt ReadAttempt(NpgsqlDataReader reader)
    {
        return new NotificationAttempt(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("delivery_id")),
            reader.GetGuid(reader.GetOrdinal("notification_id")),
            ReadTimestamp(reader, "attempted_at_utc")!.Value,
            reader.GetString(reader.GetOrdinal("status")),
            ReadNullableInt(reader, "http_status"),
            ReadNullableString(reader, "response_body"),
            ReadNullableString(reader, "error"));
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? ReadNullableInt(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetFieldValue<DateTime>(ordinal);
        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        return new DateTimeOffset(value.ToUniversalTime());
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private sealed record NotificationRow(
        Guid Id,
        string Channel,
        string Target,
        DateTimeOffset ScheduledAtUtc);

    private sealed record RoutingTargetRow(Guid Id, string Name, string Destination);

    private sealed record LegacyNotificationRow(
        Guid Id,
        string Channel,
        string Target,
        DateTimeOffset ScheduledAtUtc,
        string Status,
        string? LastError,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? UpdatedAt,
        DateTimeOffset? SentAtUtc,
        DateTimeOffset? CanceledAtUtc);

    private sealed record DispatchStateRow(
        Guid Id,
        Guid NotificationId,
        string Status,
        bool IsTargetOverride,
        string? StoredTarget,
        bool ActiveRoutingTargetExists,
        string? ActiveTargetName,
        string? ActiveDestination);
}
