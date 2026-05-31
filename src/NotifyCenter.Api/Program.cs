using System.Text.Json;
using Npgsql;
using NotifyCenter.Api.Auth;
using NotifyCenter.Api.Configuration;
using NotifyCenter.Api.Data;
using NotifyCenter.Api.Models;
using NotifyCenter.Api.Services;
using NotifyCenter.Api.Ui;

var builder = WebApplication.CreateBuilder(args);
var options = AppOptions.Load(builder.Configuration);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(options.DatabaseConnectionString));
builder.Services.AddSingleton<NotificationDatabase>();
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddHttpClient<TelegramSender>();
builder.Services.AddSingleton<NotificationSenderRegistry>();
builder.Services.AddHostedService<NotificationDispatcher>();

var app = builder.Build();

await app.Services.GetRequiredService<NotificationDatabase>().InitializeAsync();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/notifications"))
    {
        await next();
        return;
    }

    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        await WriteJsonError(context, StatusCodes.Status401Unauthorized, "Unauthorized");
        return;
    }

    var token = authorization["Bearer ".Length..].Trim();
    var jwtTokenService = context.RequestServices.GetRequiredService<JwtTokenService>();
    var principal = jwtTokenService.ValidateToken(token);
    if (principal is null)
    {
        await WriteJsonError(context, StatusCodes.Status401Unauthorized, "Unauthorized");
        return;
    }

    context.Items["principal"] = principal;
    await next();
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTimeOffset.UtcNow
}));

app.MapPost("/api/notifications", async (
    CreateNotificationRequest request,
    HttpContext context,
    AppOptions appOptions,
    NotificationSenderRegistry senderRegistry,
    NotificationRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.write") is { } forbidden)
    {
        return forbidden;
    }

    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
    {
        return Results.BadRequest(new { error = "title and body are required" });
    }

    var upsert = TryToNotificationUpsert(request, appOptions, senderRegistry, out var error);
    if (upsert is null)
    {
        return Results.BadRequest(new { error });
    }

    var result = await repository.UpsertAsync(upsert, cancellationToken);
    return Results.Json(result, statusCode: result.Action == "created" ? StatusCodes.Status201Created : StatusCodes.Status200OK);
});

app.MapPost("/api/notifications/bulk", async (
    BulkCreateNotificationsRequest request,
    HttpContext context,
    AppOptions appOptions,
    NotificationSenderRegistry senderRegistry,
    NotificationRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.write") is { } forbidden)
    {
        return forbidden;
    }

    if (request.Notifications is null || request.Notifications.Count == 0)
    {
        return Results.BadRequest(new { error = "notifications are required" });
    }

    var notifications = new List<NotificationUpsert>(request.Notifications.Count);
    foreach (var item in request.Notifications)
    {
        if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Body))
        {
            return Results.BadRequest(new { error = "title and body are required for every notification" });
        }

        var upsert = TryToNotificationUpsert(item, appOptions, senderRegistry, out var error);
        if (upsert is null)
        {
            return Results.BadRequest(new { error });
        }

        notifications.Add(upsert);
    }

    var results = await repository.UpsertManyAsync(notifications, cancellationToken);
    return Results.Ok(new BulkNotificationsResponse(
        request.Notifications.Count,
        results.Count(x => x.Action == "created"),
        results.Count(x => x.Action == "updated"),
        results.Count(x => x.Action == "skipped"),
        results));
});

app.MapGet("/api/notifications", async (
    HttpContext context,
    NotificationRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.read") is { } forbidden)
    {
        return forbidden;
    }

    var status = context.Request.Query["status"].ToString();
    var items = await repository.ListAsync(string.IsNullOrWhiteSpace(status) ? null : status, 200, cancellationToken);
    return Results.Ok(new { items });
});

app.MapGet("/api/notifications/{id:guid}", async (
    Guid id,
    HttpContext context,
    NotificationRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.read") is { } forbidden)
    {
        return forbidden;
    }

    var item = await repository.GetByIdAsync(id, cancellationToken);
    return item is null ? Results.NotFound(new { error = "Not Found" }) : Results.Ok(item);
});

app.MapGet("/api/notifications/{id:guid}/attempts", async (
    Guid id,
    HttpContext context,
    NotificationRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.read") is { } forbidden)
    {
        return forbidden;
    }

    var attempts = await repository.GetAttemptsAsync(id, cancellationToken);
    return Results.Ok(new { items = attempts });
});

app.MapPost("/api/notifications/{id:guid}/cancel", async (
    Guid id,
    HttpContext context,
    NotificationRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.cancel") is { } forbidden)
    {
        return forbidden;
    }

    var canceled = await repository.CancelAsync(id, cancellationToken);
    return canceled ? Results.Ok(new { canceled = true }) : Results.NotFound(new { error = "Not Found" });
});

app.MapPost("/api/notifications/{id:guid}/retry", async (
    Guid id,
    HttpContext context,
    NotificationRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.retry") is { } forbidden)
    {
        return forbidden;
    }

    var queued = await repository.RetryAsync(id, cancellationToken);
    return queued ? Results.Ok(new { queued = true }) : Results.NotFound(new { error = "Not Found" });
});

AdminUi.Map(app);

app.Run();

static IResult? RequireScope(HttpContext context, string scope)
{
    if (context.Items["principal"] is not AuthenticatedPrincipal principal)
    {
        return Results.Json(new { error = "Unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!principal.HasScope(scope))
    {
        return Results.Json(new { error = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);
    }

    return null;
}

static NotificationUpsert? TryToNotificationUpsert(
    CreateNotificationRequest request,
    AppOptions options,
    NotificationSenderRegistry senderRegistry,
    out string? error)
{
    var channel = senderRegistry.Normalize(request.Channel);
    if (!senderRegistry.IsSupported(channel))
    {
        error = $"unsupported notification channel: {channel}";
        return null;
    }

    var target = ResolveTarget(channel, request.Target, options);
    if (target is null)
    {
        error = "notification target is required";
        return null;
    }

    var dedupeKey = string.IsNullOrWhiteSpace(request.DedupeKey)
        ? $"manual:{Guid.NewGuid():N}"
        : request.DedupeKey.Trim();

    var sourceSystem = string.IsNullOrWhiteSpace(request.SourceSystem)
        ? "manual"
        : request.SourceSystem.Trim();

    var eventType = string.IsNullOrWhiteSpace(request.EventType)
        ? "manual.notification"
        : request.EventType.Trim();

    error = null;
    return new NotificationUpsert(
        dedupeKey,
        sourceSystem,
        eventType,
        channel,
        target,
        request.Title.Trim(),
        request.Body.Trim(),
        request.ScheduledAtUtc.ToUniversalTime(),
        MetadataToJson(request.Metadata));
}

static string? ResolveTarget(string channel, string? requestedTarget, AppOptions options)
{
    if (!string.IsNullOrWhiteSpace(requestedTarget))
    {
        return requestedTarget.Trim();
    }

    return channel == "telegram" && !string.IsNullOrWhiteSpace(options.Telegram.DefaultTarget)
        ? options.Telegram.DefaultTarget.Trim()
        : null;
}

static string MetadataToJson(JsonElement? metadata)
{
    return metadata is null || metadata.Value.ValueKind == JsonValueKind.Null
        ? "{}"
        : metadata.Value.GetRawText();
}

static async Task WriteJsonError(HttpContext context, int statusCode, string error)
{
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error }));
}
