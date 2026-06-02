using Npgsql;
using NpgsqlTypes;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Data;

public sealed class NotificationRepository(NpgsqlDataSource dataSource)
{
    private const string InsertSql = """
        INSERT INTO notification_items (
            id, dedupe_key, source_system, event_type, channel, target, title, body,
            scheduled_at_utc, status, metadata
        )
        VALUES (
            @id, @dedupe_key, @source_system, @event_type, @channel, @target, @title, @body,
            @scheduled_at_utc, 'pending', @metadata
        )
        ON CONFLICT (dedupe_key) DO NOTHING
        RETURNING id;
        """;

    private const string UpdateSql = """
        UPDATE notification_items
        SET source_system = @source_system,
            event_type = @event_type,
            channel = @channel,
            target = @target,
            title = @title,
            body = @body,
            scheduled_at_utc = @scheduled_at_utc,
            metadata = @metadata,
            last_error = NULL,
            status = CASE WHEN status = 'failed' THEN 'pending' ELSE status END,
            updated_at = now()
        WHERE dedupe_key = @dedupe_key
          AND status NOT IN ('sent', 'canceled')
        RETURNING id, status;
        """;

    private const string ItemColumns = """
        id,
        dedupe_key,
        source_system,
        event_type,
        channel,
        target,
        title,
        body,
        scheduled_at_utc,
        status,
        metadata::text AS metadata_json,
        last_error,
        created_at,
        updated_at,
        sent_at_utc,
        canceled_at_utc
        """;

    public async Task<UpsertResult> UpsertAsync(NotificationUpsert item, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await UpsertAsync(item, connection, null, cancellationToken);
    }

    public async Task<IReadOnlyList<UpsertResult>> UpsertManyAsync(
        IReadOnlyList<NotificationUpsert> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var results = new List<UpsertResult>(items.Count);

        foreach (var item in items)
        {
            results.Add(await UpsertAsync(item, connection, transaction, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
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
            filters.Add("status = @status");
            command.Parameters.AddWithValue("status", query.Status.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Channel))
        {
            filters.Add("channel = @channel");
            command.Parameters.AddWithValue("channel", query.Channel.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(query.SourceSystem))
        {
            filters.Add("source_system = @source_system");
            command.Parameters.AddWithValue("source_system", query.SourceSystem.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            filters.Add("event_type = @event_type");
            command.Parameters.AddWithValue("event_type", query.EventType.Trim());
        }

        if (query.ScheduledFromUtc.HasValue)
        {
            filters.Add("scheduled_at_utc >= @scheduled_from_utc");
            command.Parameters.AddWithValue("scheduled_from_utc", query.ScheduledFromUtc.Value.UtcDateTime);
        }

        if (query.ScheduledToUtc.HasValue)
        {
            filters.Add("scheduled_at_utc <= @scheduled_to_utc");
            command.Parameters.AddWithValue("scheduled_to_utc", query.ScheduledToUtc.Value.UtcDateTime);
        }

        var whereClause = filters.Count == 0
            ? string.Empty
            : $"\nWHERE {string.Join(" AND ", filters)}";
        command.CommandText = $"""
            SELECT {ItemColumns}
            FROM notification_items{whereClause}
            ORDER BY scheduled_at_utc DESC
            LIMIT @limit;
            """;

        return await ReadItemsAsync(command, cancellationToken);
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
                COUNT(*) FILTER (WHERE status = 'sent')::int AS sent,
                COUNT(*) FILTER (WHERE status = 'failed')::int AS failed,
                COUNT(*) FILTER (WHERE status = 'canceled')::int AS canceled,
                COUNT(*) FILTER (WHERE status = 'pending' AND scheduled_at_utc <= now())::int AS due
            FROM notification_items;
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
            0,
            reader.GetInt32(reader.GetOrdinal("sent")),
            reader.GetInt32(reader.GetOrdinal("failed")),
            reader.GetInt32(reader.GetOrdinal("canceled")),
            0,
            reader.GetInt32(reader.GetOrdinal("due")));
    }

    public async Task<NotificationItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {ItemColumns}
            FROM notification_items
            WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<IReadOnlyList<NotificationAttempt>> GetAttemptsAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, notification_id, attempted_at_utc, status, http_status, response_body, error
            FROM notification_attempts
            WHERE notification_id = @notification_id
            ORDER BY attempted_at_utc DESC;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("notification_id", notificationId);
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
            SELECT {ItemColumns}
            FROM notification_items
            WHERE status = 'pending'
              AND scheduled_at_utc <= @now_utc
            ORDER BY scheduled_at_utc ASC
            LIMIT @limit;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("now_utc", nowUtc.UtcDateTime);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        return await ReadItemsAsync(command, cancellationToken);
    }

    public async Task MarkSentAsync(Guid notificationId, int httpStatus, string responseBody, CancellationToken cancellationToken)
    {
        await AddAttemptAsync(notificationId, "sent", httpStatus, responseBody, null, cancellationToken);

        const string sql = """
            UPDATE notification_items
            SET status = 'sent',
                sent_at_utc = now(),
                last_error = NULL,
                updated_at = now()
            WHERE id = @id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", notificationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid notificationId,
        int? httpStatus,
        string? responseBody,
        string error,
        CancellationToken cancellationToken)
    {
        await AddAttemptAsync(notificationId, "failed", httpStatus, responseBody, error, cancellationToken);

        const string sql = """
            UPDATE notification_items
            SET status = 'failed',
                last_error = @error,
                updated_at = now()
            WHERE id = @id
              AND status <> 'canceled';
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("error", error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE notification_items
            SET status = 'canceled',
                canceled_at_utc = now(),
                updated_at = now()
            WHERE id = @id
              AND status IN ('pending', 'failed')
            RETURNING id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", notificationId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    public async Task<bool> RetryAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE notification_items
            SET status = 'pending',
                scheduled_at_utc = now(),
                last_error = NULL,
                updated_at = now()
            WHERE id = @id
              AND status = 'failed'
            RETURNING id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", notificationId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    private async Task<UpsertResult> UpsertAsync(
        NotificationUpsert item,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using (var insert = CreateCommand(connection, transaction, InsertSql))
        {
            insert.Parameters.AddWithValue("id", id);
            AddUpsertParameters(insert, item);
            var inserted = await insert.ExecuteScalarAsync(cancellationToken);
            if (inserted is Guid insertedId)
            {
                return new UpsertResult(insertedId, null, item.DedupeKey, "created", "pending");
            }
        }

        await using (var update = CreateCommand(connection, transaction, UpdateSql))
        {
            AddUpsertParameters(update, item);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new UpsertResult(reader.GetGuid(0), null, item.DedupeKey, "updated", reader.GetString(1));
            }
        }

        var existing = await GetByDedupeKeyAsync(item.DedupeKey, connection, transaction, cancellationToken);
        if (existing is null)
        {
            throw new InvalidOperationException("Notification upsert conflict could not be resolved");
        }

        return new UpsertResult(existing.Id, null, item.DedupeKey, "skipped", existing.Status);
    }

    private async Task<NotificationItem?> GetByDedupeKeyAsync(
        string dedupeKey,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {ItemColumns}
            FROM notification_items
            WHERE dedupe_key = @dedupe_key;
            """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("dedupe_key", dedupeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
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

    private static void AddUpsertParameters(NpgsqlCommand command, NotificationUpsert item)
    {
        command.Parameters.AddWithValue("dedupe_key", item.DedupeKey);
        command.Parameters.AddWithValue("source_system", item.SourceSystem);
        command.Parameters.AddWithValue("event_type", item.EventType);
        command.Parameters.AddWithValue("channel", item.Channel);
        command.Parameters.AddWithValue("target", item.Target);
        command.Parameters.AddWithValue("title", item.Title);
        command.Parameters.AddWithValue("body", item.Body);
        command.Parameters.AddWithValue("scheduled_at_utc", item.ScheduledAtUtc.UtcDateTime);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = item.MetadataJson;
    }

    private async Task AddAttemptAsync(
        Guid notificationId,
        string status,
        int? httpStatus,
        string? responseBody,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO notification_attempts (
                id, notification_id, attempted_at_utc, status, http_status, response_body, error
            )
            VALUES (
                @id, @notification_id, now(), @status, @http_status, @response_body, @error
            );
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("notification_id", notificationId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("http_status", (object?)httpStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("response_body", (object?)responseBody ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        var notificationId = reader.GetGuid(reader.GetOrdinal("id"));
        return new NotificationItem(
            notificationId,
            notificationId,
            reader.GetString(reader.GetOrdinal("dedupe_key")),
            reader.GetString(reader.GetOrdinal("source_system")),
            reader.GetString(reader.GetOrdinal("event_type")),
            reader.GetString(reader.GetOrdinal("channel")),
            null,
            reader.GetString(reader.GetOrdinal("target")),
            !string.IsNullOrWhiteSpace(reader.GetString(reader.GetOrdinal("target"))),
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
            null);
    }

    private static NotificationAttempt ReadAttempt(NpgsqlDataReader reader)
    {
        var notificationId = reader.GetGuid(reader.GetOrdinal("notification_id"));
        return new NotificationAttempt(
            reader.GetGuid(reader.GetOrdinal("id")),
            notificationId,
            notificationId,
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
}
