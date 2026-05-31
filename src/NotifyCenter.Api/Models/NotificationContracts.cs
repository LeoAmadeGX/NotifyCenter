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

public sealed record UpsertResult(Guid Id, string DedupeKey, string Action, string Status);

public sealed record BulkNotificationsResponse(
    int Accepted,
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<UpsertResult> Items);

public sealed record NotificationItem(
    Guid Id,
    string DedupeKey,
    string SourceSystem,
    string EventType,
    string Channel,
    string Target,
    string Title,
    string Body,
    DateTimeOffset ScheduledAtUtc,
    string Status,
    string MetadataJson,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAtUtc,
    DateTimeOffset? CanceledAtUtc);

public sealed record NotificationAttempt(
    Guid Id,
    Guid NotificationId,
    DateTimeOffset AttemptedAtUtc,
    string Status,
    int? HttpStatus,
    string? ResponseBody,
    string? Error);
