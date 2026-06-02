using Npgsql;
using NpgsqlTypes;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Data;

public sealed class RoutingTargetRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<RoutingTargetItem>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                channel,
                name,
                destination,
                is_enabled,
                sort_order,
                metadata::text AS metadata_json,
                created_at,
                updated_at
            FROM notification_routing_targets
            ORDER BY channel, sort_order, name;
            """;

        await using var command = dataSource.CreateCommand(sql);
        var items = new List<RoutingTargetItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<RoutingTargetItem> CreateAsync(
        RoutingTargetUpsertRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO notification_routing_targets (
                id,
                channel,
                name,
                destination,
                is_enabled,
                sort_order,
                metadata,
                created_at,
                updated_at
            )
            VALUES (
                @id,
                @channel,
                @name,
                @destination,
                @is_enabled,
                @sort_order,
                @metadata,
                now(),
                now()
            )
            RETURNING
                id,
                channel,
                name,
                destination,
                is_enabled,
                sort_order,
                metadata::text AS metadata_json,
                created_at,
                updated_at;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        AddParameters(command, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadItem(reader);
    }

    public async Task<RoutingTargetItem?> UpdateAsync(
        Guid id,
        RoutingTargetUpsertRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE notification_routing_targets
            SET channel = @channel,
                name = @name,
                destination = @destination,
                is_enabled = @is_enabled,
                sort_order = @sort_order,
                metadata = @metadata,
                updated_at = now()
            WHERE id = @id
            RETURNING
                id,
                channel,
                name,
                destination,
                is_enabled,
                sort_order,
                metadata::text AS metadata_json,
                created_at,
                updated_at;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        AddParameters(command, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<RoutingTargetItem?> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM notification_routing_targets
            WHERE id = @id
            RETURNING
                id,
                channel,
                name,
                destination,
                is_enabled,
                sort_order,
                metadata::text AS metadata_json,
                created_at,
                updated_at;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    private static void AddParameters(NpgsqlCommand command, RoutingTargetUpsertRequest request)
    {
        command.Parameters.AddWithValue("channel", NormalizeChannel(request.Channel));
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("destination", request.Destination.Trim());
        command.Parameters.AddWithValue("is_enabled", request.IsEnabled);
        command.Parameters.AddWithValue("sort_order", request.SortOrder);
        command.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = MetadataToJson(request.Metadata);
    }

    private static string MetadataToJson(System.Text.Json.JsonElement? metadata)
    {
        return metadata is null || metadata.Value.ValueKind == System.Text.Json.JsonValueKind.Null
            ? "{}"
            : metadata.Value.GetRawText();
    }

    private static string NormalizeChannel(string channel)
    {
        return channel.Trim().ToLowerInvariant();
    }

    private static RoutingTargetItem ReadItem(NpgsqlDataReader reader)
    {
        return new RoutingTargetItem(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("channel")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetString(reader.GetOrdinal("destination")),
            reader.GetBoolean(reader.GetOrdinal("is_enabled")),
            reader.GetInt32(reader.GetOrdinal("sort_order")),
            reader.GetString(reader.GetOrdinal("metadata_json")),
            ReadTimestamp(reader, "created_at")!.Value,
            ReadTimestamp(reader, "updated_at")!.Value);
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
