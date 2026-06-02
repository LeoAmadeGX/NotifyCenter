using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Data;

public sealed class LineSourceRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<LineSourceItem>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                source.id,
                source.source_type,
                source.source_id,
                source.display_name,
                source.picture_url,
                source.status_message,
                source.last_event_type,
                source.last_event_at_utc,
                source.first_seen_at_utc,
                source.updated_at,
                source.metadata::text AS metadata_json,
                routing_target.id AS routing_target_id,
                routing_target.name AS routing_target_name,
                routing_target.is_enabled AS routing_target_enabled
            FROM notification_line_sources AS source
            LEFT JOIN LATERAL (
                SELECT id, name, is_enabled
                FROM notification_routing_targets
                WHERE channel = 'line'
                  AND destination = source.source_id
                ORDER BY is_enabled DESC, updated_at DESC
                LIMIT 1
            ) AS routing_target ON TRUE
            ORDER BY
                CASE source.source_type
                    WHEN 'group' THEN 0
                    WHEN 'room' THEN 1
                    WHEN 'user' THEN 2
                    ELSE 3
                END,
                COALESCE(source.display_name, source.source_id);
            """;

        await using var command = dataSource.CreateCommand(sql);
        var items = new List<LineSourceItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<LineWebhookCollectResponse> UpsertFromWebhookAsync(
        LineWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        var events = payload.Events ?? [];
        var storedSources = 0;

        foreach (var webhookEvent in events)
        {
            foreach (var source in ExpandSources(payload, webhookEvent))
            {
                await UpsertSourceAsync(source, webhookEvent, cancellationToken);
                storedSources += 1;
            }
        }

        return new LineWebhookCollectResponse(events.Count, storedSources);
    }

    private async Task UpsertSourceAsync(
        LineSourceCandidate source,
        LineWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO notification_line_sources (
                id,
                source_type,
                source_id,
                display_name,
                picture_url,
                status_message,
                last_event_type,
                last_event_at_utc,
                first_seen_at_utc,
                updated_at,
                metadata
            )
            VALUES (
                @id,
                @source_type,
                @source_id,
                @display_name,
                @picture_url,
                @status_message,
                @last_event_type,
                @last_event_at_utc,
                now(),
                now(),
                @metadata
            )
            ON CONFLICT (source_type, source_id)
            DO UPDATE SET
                display_name = COALESCE(EXCLUDED.display_name, notification_line_sources.display_name),
                picture_url = COALESCE(EXCLUDED.picture_url, notification_line_sources.picture_url),
                status_message = COALESCE(EXCLUDED.status_message, notification_line_sources.status_message),
                last_event_type = EXCLUDED.last_event_type,
                last_event_at_utc = COALESCE(EXCLUDED.last_event_at_utc, notification_line_sources.last_event_at_utc),
                updated_at = now(),
                metadata = notification_line_sources.metadata || EXCLUDED.metadata;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("source_type", source.SourceType);
        command.Parameters.AddWithValue("source_id", source.SourceId);
        command.Parameters.AddWithValue("display_name", (object?)source.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("picture_url", (object?)source.PictureUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("status_message", (object?)source.StatusMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("last_event_type", (object?)NormalizeEventType(webhookEvent.Type) ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "last_event_at_utc",
            (object?)ReadEventTimestamp(webhookEvent.Timestamp)?.UtcDateTime ?? DBNull.Value);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = source.MetadataJson;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<LineSourceCandidate> ExpandSources(
        LineWebhookPayload payload,
        LineWebhookEvent webhookEvent)
    {
        if (webhookEvent.Source is null)
        {
            return [];
        }

        var source = webhookEvent.Source;
        var normalizedType = NormalizeSourceType(source.Type);
        var items = new List<LineSourceCandidate>();

        if (normalizedType == "user" && !string.IsNullOrWhiteSpace(source.UserId))
        {
            items.Add(CreateCandidate("user", source.UserId, payload, webhookEvent, null, null));
        }
        else if (normalizedType == "group" && !string.IsNullOrWhiteSpace(source.GroupId))
        {
            items.Add(CreateCandidate("group", source.GroupId, payload, webhookEvent, null, null));
        }
        else if (normalizedType == "room" && !string.IsNullOrWhiteSpace(source.RoomId))
        {
            items.Add(CreateCandidate("room", source.RoomId, payload, webhookEvent, null, null));
        }

        if (normalizedType is "group" or "room" && !string.IsNullOrWhiteSpace(source.UserId))
        {
            items.Add(CreateCandidate(
                "user",
                source.UserId,
                payload,
                webhookEvent,
                normalizedType,
                normalizedType == "group" ? source.GroupId : source.RoomId));
        }

        return items;
    }

    private static LineSourceCandidate CreateCandidate(
        string sourceType,
        string sourceId,
        LineWebhookPayload payload,
        LineWebhookEvent webhookEvent,
        string? contextType,
        string? contextId)
    {
        var metadata = new Dictionary<string, string>();
        AddMetadata(metadata, "lastDestination", payload.Destination);
        AddMetadata(metadata, "lastWebhookEventId", webhookEvent.WebhookEventId);
        AddMetadata(metadata, "lastContextType", contextType);
        AddMetadata(metadata, "lastContextId", contextId);

        return new LineSourceCandidate(
            sourceType,
            sourceId.Trim(),
            null,
            null,
            null,
            JsonSerializer.Serialize(metadata));
    }

    private static void AddMetadata(IDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value.Trim();
        }
    }

    private static string? NormalizeEventType(string? eventType)
    {
        return string.IsNullOrWhiteSpace(eventType) ? null : eventType.Trim().ToLowerInvariant();
    }

    private static string? NormalizeSourceType(string? sourceType)
    {
        return string.IsNullOrWhiteSpace(sourceType) ? null : sourceType.Trim().ToLowerInvariant();
    }

    private static DateTimeOffset? ReadEventTimestamp(long? timestamp)
    {
        return timestamp.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value)
            : null;
    }

    private static LineSourceItem ReadItem(NpgsqlDataReader reader)
    {
        return new LineSourceItem(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetString(reader.GetOrdinal("source_id")),
            ReadNullableString(reader, "display_name"),
            ReadNullableString(reader, "picture_url"),
            ReadNullableString(reader, "status_message"),
            ReadNullableString(reader, "last_event_type"),
            ReadTimestamp(reader, "last_event_at_utc"),
            ReadTimestamp(reader, "first_seen_at_utc")!.Value,
            ReadTimestamp(reader, "updated_at")!.Value,
            reader.GetString(reader.GetOrdinal("metadata_json")),
            ReadNullableGuid(reader, "routing_target_id"),
            ReadNullableString(reader, "routing_target_name"),
            ReadNullableBool(reader, "routing_target_enabled"));
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? ReadNullableGuid(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static bool? ReadNullableBool(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
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

    private sealed record LineSourceCandidate(
        string SourceType,
        string SourceId,
        string? DisplayName,
        string? PictureUrl,
        string? StatusMessage,
        string MetadataJson);
}
