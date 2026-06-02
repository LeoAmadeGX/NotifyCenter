using System.Text.Json;

namespace NotifyCenter.Api.Models;

public sealed record LineWebhookPayload(
    string? Destination,
    IReadOnlyList<LineWebhookEvent>? Events);

public sealed record LineWebhookEvent(
    string? Type,
    string? Mode,
    long? Timestamp,
    string? WebhookEventId,
    LineWebhookSource? Source,
    JsonElement? DeliveryContext);

public sealed record LineWebhookSource(
    string? Type,
    string? UserId,
    string? GroupId,
    string? RoomId);

public sealed record LineWebhookCollectResponse(
    int AcceptedEvents,
    int StoredSources);

public sealed record LineSourceItem(
    Guid Id,
    string SourceType,
    string SourceId,
    string? DisplayName,
    string? PictureUrl,
    string? StatusMessage,
    string? LastEventType,
    DateTimeOffset? LastEventAtUtc,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset UpdatedAt,
    string MetadataJson,
    Guid? RoutingTargetId,
    string? RoutingTargetName,
    bool? RoutingTargetEnabled);
