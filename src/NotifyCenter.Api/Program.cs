using System.Text.Json;
using System.Globalization;
using Npgsql;
using NotifyCenter.Api.Auth;
using NotifyCenter.Api.Configuration;
using NotifyCenter.Api.Data;
using NotifyCenter.Api.Models;
using NotifyCenter.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var options = AppOptions.Load(builder.Configuration);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(options.DatabaseConnectionString));
builder.Services.AddSingleton<NotificationDatabase>();
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<NotificationDeliveryRepository>();
builder.Services.AddSingleton<RoutingTargetRepository>();
builder.Services.AddSingleton<AdminUserRepository>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<AdminSessionService>();
builder.Services.AddHttpClient<TelegramSender>();
builder.Services.AddSingleton<NotificationSenderRegistry>();
builder.Services.AddSingleton<AdminDashboardEventBroadcaster>();
builder.Services.AddHostedService<NotificationDispatcher>();

var app = builder.Build();

await app.Services.GetRequiredService<NotificationDatabase>().InitializeAsync();
await app.Services.GetRequiredService<AdminUserRepository>().EnsureBootstrapAdminAsync(CancellationToken.None);
await app.Services.GetRequiredService<NotificationDeliveryRepository>().MigrateLegacyDataAsync(CancellationToken.None);

app.Use(async (context, next) =>
{
    if (!IsProtectedApiPath(context.Request.Path))
    {
        await next();
        return;
    }

    var jwtTokenService = context.RequestServices.GetRequiredService<JwtTokenService>();
    var adminSessionService = context.RequestServices.GetRequiredService<AdminSessionService>();
    var adminRepository = context.RequestServices.GetRequiredService<AdminUserRepository>();
    var principal = TryGetBearerPrincipal(context, jwtTokenService) ??
        await adminSessionService.GetCurrentPrincipalAsync(
            context,
            jwtTokenService,
            adminRepository,
            context.RequestAborted);

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

app.MapPost("/api/admin/login", async (
    AdminLoginRequest request,
    HttpContext context,
    AppOptions appOptions,
    AdminUserRepository repository,
    PasswordHasher passwordHasher,
    AdminSessionService adminSessionService,
    JwtTokenService jwtTokenService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { error = "username and password are required" });
    }

    var user = await repository.GetByUsernameAsync(request.Username, cancellationToken);
    if (user is null ||
        !passwordHasher.Verify(
            request.Password,
            user.PasswordHash,
            user.PasswordSalt,
            user.PasswordIterations))
    {
        return Results.Json(new { error = "Invalid username or password" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    await repository.RecordLoginAsync(user.Id, cancellationToken);
    adminSessionService.SignIn(context, jwtTokenService, user);
    return Results.Ok(ToAdminSessionResponse(user, appOptions));
});

app.MapPost("/api/admin/logout", (HttpContext context, AdminSessionService adminSessionService) =>
{
    adminSessionService.SignOut(context);
    return Results.NoContent();
});

app.MapGet("/api/admin/session", async (
    HttpContext context,
    AppOptions appOptions,
    AdminSessionService adminSessionService,
    AdminUserRepository repository,
    JwtTokenService jwtTokenService,
    CancellationToken cancellationToken) =>
{
    var user = await adminSessionService.GetCurrentUserAsync(
        context,
        jwtTokenService,
        repository,
        cancellationToken);

    return user is null
        ? Results.Json(new { error = "Unauthorized" }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(ToAdminSessionResponse(user, appOptions));
});

app.MapPost("/api/admin/change-password", async (
    AdminChangePasswordRequest request,
    HttpContext context,
    AppOptions appOptions,
    AdminSessionService adminSessionService,
    AdminUserRepository repository,
    PasswordHasher passwordHasher,
    JwtTokenService jwtTokenService,
    CancellationToken cancellationToken) =>
{
    var user = await adminSessionService.GetCurrentUserAsync(
        context,
        jwtTokenService,
        repository,
        cancellationToken);

    if (user is null)
    {
        return Results.Json(new { error = "Unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
        string.IsNullOrWhiteSpace(request.NewPassword) ||
        string.IsNullOrWhiteSpace(request.ConfirmPassword))
    {
        return Results.BadRequest(new { error = "currentPassword, newPassword, and confirmPassword are required" });
    }

    if (!passwordHasher.Verify(
            request.CurrentPassword,
            user.PasswordHash,
            user.PasswordSalt,
            user.PasswordIterations))
    {
        return Results.BadRequest(new { error = "Current password is incorrect" });
    }

    if (request.NewPassword != request.ConfirmPassword)
    {
        return Results.BadRequest(new { error = "New password confirmation does not match" });
    }

    if (request.NewPassword.Length < 8)
    {
        return Results.BadRequest(new { error = "New password must be at least 8 characters" });
    }

    if (request.NewPassword == request.CurrentPassword)
    {
        return Results.BadRequest(new { error = "New password must be different from the current password" });
    }

    var password = passwordHasher.HashPassword(request.NewPassword);
    await repository.UpdatePasswordAsync(user.Id, password, cancellationToken);

    var updatedUser = user with
    {
        PasswordHash = password.Hash,
        PasswordSalt = password.Salt,
        PasswordIterations = password.Iterations,
        MustChangePassword = false,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    adminSessionService.SignIn(context, jwtTokenService, updatedUser);
    return Results.Ok(ToAdminSessionResponse(updatedUser, appOptions));
});

app.MapPost("/api/notifications", async (
    CreateNotificationRequest request,
    HttpContext context,
    NotificationSenderRegistry senderRegistry,
    NotificationRepository notificationRepository,
    NotificationDeliveryRepository deliveryRepository,
    AdminDashboardEventBroadcaster eventBroadcaster,
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

    var upsert = TryToNotificationUpsert(request, senderRegistry, out var error);
    if (upsert is null)
    {
        return Results.BadRequest(new { error });
    }

    var result = await notificationRepository.UpsertAsync(upsert, cancellationToken);
    if (result.Action is "created" or "updated")
    {
        var deliveryId = await deliveryRepository.RematerializeForNotificationAsync(result.NotificationId, cancellationToken);
        result = result with { DeliveryId = deliveryId };
        eventBroadcaster.Publish("deliveries_changed", deliveryId, upsert.Channel);
    }

    return Results.Json(result, statusCode: result.Action == "created" ? StatusCodes.Status201Created : StatusCodes.Status200OK);
});

app.MapPost("/api/notifications/bulk", async (
    BulkCreateNotificationsRequest request,
    HttpContext context,
    NotificationSenderRegistry senderRegistry,
    NotificationRepository notificationRepository,
    NotificationDeliveryRepository deliveryRepository,
    AdminDashboardEventBroadcaster eventBroadcaster,
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

        var upsert = TryToNotificationUpsert(item, senderRegistry, out var error);
        if (upsert is null)
        {
            return Results.BadRequest(new { error });
        }

        notifications.Add(upsert);
    }

    var rawResults = await notificationRepository.UpsertManyAsync(notifications, cancellationToken);
    var results = new List<UpsertResult>(rawResults.Count);
    foreach (var result in rawResults)
    {
        if (result.Action is "created" or "updated")
        {
            var deliveryId = await deliveryRepository.RematerializeForNotificationAsync(result.NotificationId, cancellationToken);
            results.Add(result with { DeliveryId = deliveryId });
        }
        else
        {
            results.Add(result);
        }
    }

    eventBroadcaster.Publish("deliveries_changed");

    return Results.Ok(new BulkNotificationsResponse(
        request.Notifications.Count,
        results.Count(x => x.Action == "created"),
        results.Count(x => x.Action == "updated"),
        results.Count(x => x.Action == "skipped"),
        results));
});

app.MapGet("/api/notifications", async (
    HttpContext context,
    NotificationDeliveryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.read") is { } forbidden)
    {
        return forbidden;
    }

    var query = new NotificationListQuery(
        NormalizeFilter(context.Request.Query["status"].ToString()),
        NormalizeFilter(context.Request.Query["channel"].ToString()),
        NormalizeFilter(context.Request.Query["sourceSystem"].ToString()),
        NormalizeFilter(context.Request.Query["eventType"].ToString()),
        NormalizeFilter(context.Request.Query["messageQuery"].ToString()),
        ReadDateTimeOffset(context.Request.Query["scheduledFromUtc"].ToString()),
        ReadDateTimeOffset(context.Request.Query["scheduledToUtc"].ToString()),
        ReadLimit(context.Request.Query["limit"].ToString()));
    var items = await repository.ListAsync(query, cancellationToken);
    return Results.Ok(new { items });
});

app.MapGet("/api/notifications/filter-options", async (
    HttpContext context,
    NotificationDeliveryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.read") is { } forbidden)
    {
        return forbidden;
    }

    var options = await repository.GetFilterOptionsAsync(cancellationToken);
    return Results.Ok(options);
});

app.MapGet("/api/notifications/stats", async (
    HttpContext context,
    NotificationDeliveryRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.read") is { } forbidden)
    {
        return forbidden;
    }

    var stats = await repository.GetStatsAsync(cancellationToken);
    return Results.Ok(stats);
});

app.MapGet("/api/notifications/{id:guid}", async (
    Guid id,
    HttpContext context,
    NotificationDeliveryRepository repository,
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
    NotificationDeliveryRepository repository,
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
    NotificationDeliveryRepository repository,
    AdminDashboardEventBroadcaster eventBroadcaster,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.cancel") is { } forbidden)
    {
        return forbidden;
    }

    var item = await repository.GetByIdAsync(id, cancellationToken);
    if (item is null)
    {
        return Results.NotFound(new { error = "Notification not found" });
    }

    if (item.Status is not ("pending" or "pending_no_target" or "failed"))
    {
        return Results.Json(
            new { error = $"Only pending, pending_no_target, or failed deliveries can be canceled. Current status: {item.Status}" },
            statusCode: StatusCodes.Status409Conflict);
    }

    var canceled = await repository.CancelAsync(id, cancellationToken);
    if (canceled is not null)
    {
        eventBroadcaster.Publish("deliveries_changed", canceled.Id, canceled.Channel);
    }

    return canceled is not null
        ? Results.Ok(new { canceled = true })
        : Results.Json(new { error = "Notification state changed before cancel could be applied" }, statusCode: StatusCodes.Status409Conflict);
});

app.MapPost("/api/notifications/{id:guid}/retry", async (
    Guid id,
    HttpContext context,
    NotificationDeliveryRepository repository,
    AdminDashboardEventBroadcaster eventBroadcaster,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.retry") is { } forbidden)
    {
        return forbidden;
    }

    var item = await repository.GetByIdAsync(id, cancellationToken);
    if (item is null)
    {
        return Results.NotFound(new { error = "Notification not found" });
    }

    if (!string.Equals(item.Status, "failed", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new { error = $"Only failed notifications can be retried. Current status: {item.Status}" },
            statusCode: StatusCodes.Status409Conflict);
    }

    var queued = await repository.RetryAsync(id, cancellationToken);
    if (queued is not null)
    {
        eventBroadcaster.Publish("deliveries_changed", queued.Id, queued.Channel);
    }

    return queued is not null
        ? Results.Ok(new
        {
            queued = string.Equals(queued.Status, "pending", StringComparison.OrdinalIgnoreCase),
            skipped = string.Equals(queued.Status, "skipped_no_target", StringComparison.OrdinalIgnoreCase),
            status = queued.Status
        })
        : Results.Json(new { error = "Notification state changed before retry could be applied" }, statusCode: StatusCodes.Status409Conflict);
});

app.MapGet("/api/routing-targets", async (
    HttpContext context,
    RoutingTargetRepository repository,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.admin") is { } forbidden)
    {
        return forbidden;
    }

    var items = await repository.ListAsync(cancellationToken);
    return Results.Ok(new { items });
});

app.MapPost("/api/routing-targets", async (
    RoutingTargetUpsertRequest request,
    HttpContext context,
    RoutingTargetRepository repository,
    NotificationDeliveryRepository deliveryRepository,
    AdminDashboardEventBroadcaster eventBroadcaster,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.admin") is { } forbidden)
    {
        return forbidden;
    }

    if (!TryValidateRoutingTarget(request, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var item = await repository.CreateAsync(request, cancellationToken);
    await deliveryRepository.RebuildScheduledDeliveriesForChannelAsync(item.Channel, cancellationToken);
    eventBroadcaster.Publish("deliveries_changed", null, item.Channel);
    return Results.Created($"/api/routing-targets/{item.Id}", item);
});

app.MapPatch("/api/routing-targets/{id:guid}", async (
    Guid id,
    RoutingTargetUpsertRequest request,
    HttpContext context,
    RoutingTargetRepository repository,
    NotificationDeliveryRepository deliveryRepository,
    AdminDashboardEventBroadcaster eventBroadcaster,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.admin") is { } forbidden)
    {
        return forbidden;
    }

    if (!TryValidateRoutingTarget(request, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var item = await repository.UpdateAsync(id, request, cancellationToken);
    if (item is null)
    {
        return Results.NotFound(new { error = "Routing target not found" });
    }

    await deliveryRepository.RebuildScheduledDeliveriesForChannelAsync(item.Channel, cancellationToken);
    eventBroadcaster.Publish("deliveries_changed", null, item.Channel);
    return Results.Ok(item);
});

app.MapDelete("/api/routing-targets/{id:guid}", async (
    Guid id,
    HttpContext context,
    RoutingTargetRepository repository,
    NotificationDeliveryRepository deliveryRepository,
    AdminDashboardEventBroadcaster eventBroadcaster,
    CancellationToken cancellationToken) =>
{
    if (RequireScope(context, "notifications.admin") is { } forbidden)
    {
        return forbidden;
    }

    var item = await repository.DeleteAsync(id, cancellationToken);
    if (item is null)
    {
        return Results.NotFound(new { error = "Routing target not found" });
    }

    await deliveryRepository.RebuildScheduledDeliveriesForChannelAsync(item.Channel, cancellationToken);
    eventBroadcaster.Publish("deliveries_changed", null, item.Channel);
    return Results.NoContent();
});

app.MapGet("/api/admin/events", async (
    HttpContext context,
    AdminDashboardEventBroadcaster eventBroadcaster,
    CancellationToken cancellationToken) =>
{
    if (!await EnsureScopeAsync(context, "notifications.admin"))
    {
        return;
    }

    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers.Append("X-Accel-Buffering", "no");
    context.Response.ContentType = "text/event-stream";

    await context.Response.WriteAsync(": connected\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);

    await using var subscription = eventBroadcaster.Subscribe(cancellationToken);
    using var keepAliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(20));

    while (!cancellationToken.IsCancellationRequested)
    {
        var waitForData = subscription.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var waitForKeepAlive = keepAliveTimer.WaitForNextTickAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(waitForData, waitForKeepAlive);

        if (completed == waitForKeepAlive)
        {
            if (!await waitForKeepAlive)
            {
                break;
            }

            await context.Response.WriteAsync(": keepalive\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            continue;
        }

        if (!await waitForData)
        {
            break;
        }

        while (subscription.Reader.TryRead(out var @event))
        {
            var json = JsonSerializer.Serialize(@event);
            await context.Response.WriteAsync($"event: dashboard\ndata: {json}\n\n", cancellationToken);
        }

        await context.Response.Body.FlushAsync(cancellationToken);
    }
});

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

static AuthenticatedPrincipal? TryGetBearerPrincipal(HttpContext context, JwtTokenService jwtTokenService)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var token = authorization["Bearer ".Length..].Trim();
    return jwtTokenService.ValidateToken(token);
}

static NotificationUpsert? TryToNotificationUpsert(
    CreateNotificationRequest request,
    NotificationSenderRegistry senderRegistry,
    out string? error)
{
    var channel = senderRegistry.Normalize(request.Channel);
    if (!senderRegistry.IsSupported(channel))
    {
        error = $"unsupported notification channel: {channel}";
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
        NormalizeOverrideTarget(request.Target),
        request.Title.Trim(),
        request.Body.Trim(),
        request.ScheduledAtUtc.ToUniversalTime(),
        MetadataToJson(request.Metadata));
}

static string NormalizeOverrideTarget(string? requestedTarget)
{
    return string.IsNullOrWhiteSpace(requestedTarget) ? string.Empty : requestedTarget.Trim();
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

static AdminSessionResponse ToAdminSessionResponse(AdminUser user, AppOptions appOptions)
{
    return new AdminSessionResponse(
        user.Username,
        user.MustChangePassword,
        string.IsNullOrWhiteSpace(appOptions.Telegram.DefaultTarget)
            ? null
            : appOptions.Telegram.DefaultTarget.Trim());
}

static string? NormalizeFilter(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return value.Trim();
}

static int ReadLimit(string? value)
{
    return int.TryParse(value, out var parsed) ? parsed : 200;
}

static DateTimeOffset? ReadDateTimeOffset(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return DateTimeOffset.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind,
        out var parsed)
        ? parsed.ToUniversalTime()
        : null;
}

static bool IsProtectedApiPath(PathString path)
{
    return path.StartsWithSegments("/api/notifications")
        || path.StartsWithSegments("/api/routing-targets")
        || path.StartsWithSegments("/api/admin/events");
}

static bool TryValidateRoutingTarget(RoutingTargetUpsertRequest request, out string? error)
{
    if (string.IsNullOrWhiteSpace(request.Channel))
    {
        error = "channel is required";
        return false;
    }

    if (string.IsNullOrWhiteSpace(request.Name))
    {
        error = "name is required";
        return false;
    }

    if (string.IsNullOrWhiteSpace(request.Destination))
    {
        error = "destination is required";
        return false;
    }

    error = null;
    return true;
}

static async Task<bool> EnsureScopeAsync(HttpContext context, string scope)
{
    if (context.Items["principal"] is not AuthenticatedPrincipal principal)
    {
        await WriteJsonError(context, StatusCodes.Status401Unauthorized, "Unauthorized");
        return false;
    }

    if (!principal.HasScope(scope))
    {
        await WriteJsonError(context, StatusCodes.Status403Forbidden, "Forbidden");
        return false;
    }

    return true;
}
