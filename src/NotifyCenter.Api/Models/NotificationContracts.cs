using System.Text.Json;

namespace NotifyCenter.Api.Models;

public sealed record CreateNotificationRequest(
    string? DedupeKey,
    string? SourceSystem,
    string? EventType,
    string? Channel,
    string? Target,
    string Title,
    string Body,
    DateTimeOffset ScheduledAtUtc,
    JsonElement? Metadata);

public sealed record BulkCreateNotificationsRequest(
    IReadOnlyList<CreateNotificationRequest> Notifications);

public sealed record NotificationUpsert(
    string DedupeKey,
    string SourceSystem,
    string EventType,
    string Channel,
    string Target,
    string Title,
    string Body,
    DateTimeOffset ScheduledAtUtc,
    string MetadataJson);

public sealed record UpsertResult(
    Guid NotificationId,
    Guid? DeliveryId,
    string DedupeKey,
    string Action,
    string Status);

public sealed record BulkNotificationsResponse(
    int Accepted,
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<UpsertResult> Items);

public sealed record NotificationItem(
    Guid Id,
    Guid NotificationId,
    string DedupeKey,
    string SourceSystem,
    string EventType,
    string Channel,
    string? TargetName,
    string? Target,
    bool IsTargetOverride,
    string Title,
    string Body,
    DateTimeOffset ScheduledAtUtc,
    string Status,
    string MetadataJson,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAtUtc,
    DateTimeOffset? CanceledAtUtc,
    DateTimeOffset? SkippedAtUtc);

public sealed record NotificationAttempt(
    Guid Id,
    Guid DeliveryId,
    Guid NotificationId,
    DateTimeOffset AttemptedAtUtc,
    string Status,
    int? HttpStatus,
    string? ResponseBody,
    string? Error);

public sealed record RoutingTargetUpsertRequest(
    string Channel,
    string Name,
    string Destination,
    bool IsEnabled,
    int SortOrder,
    JsonElement? Metadata);

public sealed record RoutingTargetItem(
    Guid Id,
    string Channel,
    string Name,
    string Destination,
    bool IsEnabled,
    int SortOrder,
    string MetadataJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
