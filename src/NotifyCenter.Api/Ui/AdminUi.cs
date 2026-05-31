using System.Net;
using System.Text;
using NotifyCenter.Api.Auth;
using NotifyCenter.Api.Configuration;
using NotifyCenter.Api.Data;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Ui;

public static class AdminUi
{
    private const string AdminCookieName = "notifycenter_admin";

    public static void Map(WebApplication app)
    {
        app.MapGet("/ui/login", () => Results.Content(LoginPage(), "text/html; charset=utf-8"));

        app.MapPost("/ui/login", async (
            HttpContext context,
            AppOptions options,
            JwtTokenService jwtTokenService) =>
        {
            if (string.IsNullOrWhiteSpace(options.AdminPassword))
            {
                return Results.Content(
                    LoginPage("NOTIFICATION_ADMIN_PASSWORD is not configured."),
                    "text/html; charset=utf-8");
            }

            var form = await context.Request.ReadFormAsync();
            var password = form["password"].ToString();
            if (!SlowEquals(password, options.AdminPassword))
            {
                return Results.Content(LoginPage("Invalid password."), "text/html; charset=utf-8");
            }

            var token = jwtTokenService.CreateToken(
                "admin-ui",
                ["notifications.admin"],
                TimeSpan.FromHours(8));

            context.Response.Cookies.Append(
                AdminCookieName,
                token.Token,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = token.ExpiresAtUtc
                });

            return Results.Redirect("/ui");
        });

        app.MapPost("/ui/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(AdminCookieName);
            return Results.Redirect("/ui/login");
        });

        app.MapGet("/ui", async (
            HttpContext context,
            NotificationRepository repository,
            JwtTokenService jwtTokenService,
            CancellationToken cancellationToken) =>
        {
            if (!IsAdmin(context, jwtTokenService))
            {
                return Results.Redirect("/ui/login");
            }

            var status = context.Request.Query["status"].ToString();
            if (string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                status = string.Empty;
            }

            var items = await repository.ListAsync(
                string.IsNullOrWhiteSpace(status) ? null : status,
                200,
                cancellationToken);

            return Results.Content(ListPage(items, status), "text/html; charset=utf-8");
        });

        app.MapGet("/ui/notifications/{id:guid}", async (
            Guid id,
            HttpContext context,
            NotificationRepository repository,
            JwtTokenService jwtTokenService,
            CancellationToken cancellationToken) =>
        {
            if (!IsAdmin(context, jwtTokenService))
            {
                return Results.Redirect("/ui/login");
            }

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound("Notification not found");
            }

            var attempts = await repository.GetAttemptsAsync(id, cancellationToken);
            return Results.Content(DetailPage(item, attempts), "text/html; charset=utf-8");
        });

        app.MapPost("/ui/notifications/{id:guid}/cancel", async (
            Guid id,
            HttpContext context,
            NotificationRepository repository,
            JwtTokenService jwtTokenService,
            CancellationToken cancellationToken) =>
        {
            if (!IsAdmin(context, jwtTokenService))
            {
                return Results.Redirect("/ui/login");
            }

            await repository.CancelAsync(id, cancellationToken);
            return Results.Redirect("/ui");
        });

        app.MapPost("/ui/notifications/{id:guid}/retry", async (
            Guid id,
            HttpContext context,
            NotificationRepository repository,
            JwtTokenService jwtTokenService,
            CancellationToken cancellationToken) =>
        {
            if (!IsAdmin(context, jwtTokenService))
            {
                return Results.Redirect("/ui/login");
            }

            await repository.RetryAsync(id, cancellationToken);
            return Results.Redirect($"/ui/notifications/{id}");
        });
    }

    private static bool IsAdmin(HttpContext context, JwtTokenService jwtTokenService)
    {
        var token = context.Request.Cookies[AdminCookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var principal = jwtTokenService.ValidateToken(token);
        return principal?.HasScope("notifications.admin") == true;
    }

    private static string LoginPage(string? error = null)
    {
        var errorHtml = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : $"""<p class="error">{Html(error)}</p>""";

        return Layout("Login", $"""
            <main class="login">
              <h1>NotifyCenter Admin</h1>
              {errorHtml}
              <form method="post" action="/ui/login">
                <label>Password</label>
                <input type="password" name="password" autofocus />
                <button type="submit">Login</button>
              </form>
            </main>
            """);
    }

    private static string ListPage(IReadOnlyList<NotificationItem> items, string? status)
    {
        var rows = new StringBuilder();
        foreach (var item in items)
        {
            rows.Append($"""
                <tr>
                  <td><a href="/ui/notifications/{item.Id}">{Html(item.Title)}</a></td>
                  <td>{Html(item.Status)}</td>
                  <td>{Html(item.Channel)}</td>
                  <td>{Html(item.ScheduledAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))}</td>
                  <td>{Html(item.SourceSystem)}</td>
                  <td>{Html(item.EventType)}</td>
                  <td>{Html(item.LastError ?? "")}</td>
                  <td class="actions">
                    <form method="post" action="/ui/notifications/{item.Id}/cancel">
                      <button type="submit">Cancel</button>
                    </form>
                    <form method="post" action="/ui/notifications/{item.Id}/retry">
                      <button type="submit">Retry</button>
                    </form>
                  </td>
                </tr>
                """);
        }

        var selected = string.IsNullOrWhiteSpace(status) ? "all" : status;
        return Layout("Notifications", $"""
            <header>
              <h1>Notifications</h1>
              <form method="post" action="/ui/logout"><button type="submit">Logout</button></form>
            </header>
            <nav>
              {FilterLink("all", selected)}
              {FilterLink("pending", selected)}
              {FilterLink("sent", selected)}
              {FilterLink("failed", selected)}
              {FilterLink("canceled", selected)}
            </nav>
            <table>
              <thead>
                <tr>
                  <th>Title</th>
                  <th>Status</th>
                  <th>Channel</th>
                  <th>Scheduled UTC</th>
                  <th>Source System</th>
                  <th>Event Type</th>
                  <th>Last Error</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>{rows}</tbody>
            </table>
            """);
    }

    private static string DetailPage(NotificationItem item, IReadOnlyList<NotificationAttempt> attempts)
    {
        var rows = new StringBuilder();
        foreach (var attempt in attempts)
        {
            rows.Append($"""
                <tr>
                  <td>{Html(attempt.AttemptedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))}</td>
                  <td>{Html(attempt.Status)}</td>
                  <td>{Html(attempt.HttpStatus?.ToString() ?? "")}</td>
                  <td>{Html(attempt.Error ?? "")}</td>
                  <td><pre>{Html(attempt.ResponseBody ?? "")}</pre></td>
                </tr>
                """);
        }

        return Layout("Notification Detail", $"""
            <header>
              <h1>{Html(item.Title)}</h1>
              <a href="/ui">Back</a>
            </header>
            <section class="detail">
              <dl>
                <dt>Status</dt><dd>{Html(item.Status)}</dd>
                <dt>Source System</dt><dd>{Html(item.SourceSystem)}</dd>
                <dt>Event Type</dt><dd>{Html(item.EventType)}</dd>
                <dt>Channel</dt><dd>{Html(item.Channel)}</dd>
                <dt>Target</dt><dd>{Html(item.Target)}</dd>
                <dt>Scheduled UTC</dt><dd>{Html(item.ScheduledAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))}</dd>
                <dt>Dedupe Key</dt><dd>{Html(item.DedupeKey)}</dd>
                <dt>Body</dt><dd><pre>{Html(item.Body)}</pre></dd>
                <dt>Metadata</dt><dd><pre>{Html(item.MetadataJson)}</pre></dd>
                <dt>Last Error</dt><dd>{Html(item.LastError ?? "")}</dd>
              </dl>
              <div class="actions">
                <form method="post" action="/ui/notifications/{item.Id}/cancel"><button type="submit">Cancel</button></form>
                <form method="post" action="/ui/notifications/{item.Id}/retry"><button type="submit">Retry</button></form>
              </div>
            </section>
            <h2>Attempts</h2>
            <table>
              <thead><tr><th>Attempted UTC</th><th>Status</th><th>HTTP</th><th>Error</th><th>Response</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            """);
    }

    private static string FilterLink(string value, string selected)
    {
        var active = string.Equals(value, selected, StringComparison.OrdinalIgnoreCase) ? "active" : string.Empty;
        return $"""<a class="{active}" href="/ui?status={value}">{value}</a>""";
    }

    private static string Layout(string title, string body)
    {
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{Html(title)}}</title>
              <style>
                body { margin: 0; font-family: system-ui, -apple-system, Segoe UI, sans-serif; background: #f7f8fa; color: #16181d; }
                header { display: flex; justify-content: space-between; align-items: center; gap: 16px; padding: 20px 24px; background: #fff; border-bottom: 1px solid #dde1e7; }
                h1 { margin: 0; font-size: 24px; }
                h2 { margin: 24px 24px 12px; }
                nav { display: flex; gap: 8px; padding: 16px 24px; }
                nav a { padding: 7px 11px; border: 1px solid #cfd5dd; border-radius: 6px; text-decoration: none; color: #1f2937; background: #fff; }
                nav a.active { background: #111827; color: #fff; border-color: #111827; }
                table { width: calc(100% - 48px); margin: 0 24px 24px; border-collapse: collapse; background: #fff; border: 1px solid #dde1e7; }
                th, td { padding: 10px; border-bottom: 1px solid #edf0f4; text-align: left; vertical-align: top; font-size: 14px; }
                th { background: #f2f4f7; font-weight: 700; }
                pre { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
                button { padding: 7px 11px; border: 1px solid #cfd5dd; border-radius: 6px; background: #fff; cursor: pointer; }
                .actions { display: flex; gap: 8px; flex-wrap: wrap; }
                .detail { margin: 24px; padding: 18px; background: #fff; border: 1px solid #dde1e7; border-radius: 8px; }
                dl { display: grid; grid-template-columns: 160px 1fr; gap: 10px 16px; margin: 0 0 16px; }
                dt { font-weight: 700; color: #4b5563; }
                dd { margin: 0; overflow-wrap: anywhere; }
                .login { max-width: 360px; margin: 15vh auto; padding: 24px; background: #fff; border: 1px solid #dde1e7; border-radius: 8px; }
                .login form { display: grid; gap: 10px; }
                .login input { padding: 9px 10px; border: 1px solid #cfd5dd; border-radius: 6px; }
                .error { color: #b42318; }
              </style>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static bool SlowEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < left.Length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
    }
}
