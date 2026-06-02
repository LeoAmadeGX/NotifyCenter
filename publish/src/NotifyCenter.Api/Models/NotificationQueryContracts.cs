namespace NotifyCenter.Api.Models;

public sealed record NotificationListQuery(
    string? Status,
    string? Channel,
    string? SourceSystem,
    string? EventType,
    string? MessageQuery,
    DateTimeOffset? ScheduledFromUtc,
    DateTimeOffset? ScheduledToUtc,
    int Limit);

public sealed record NotificationFilterOptionsResponse(
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> SourceSystems,
    IReadOnlyList<string> EventTypes);

public sealed record NotificationStatsResponse(
    int Total,
    int Pending,
    int PendingNoTarget,
    int Sent,
    int Failed,
    int Canceled,
    int Skipped,
    int Due);
