using NotifyCenter.Api.Data;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Auth;

public sealed class AdminSessionService
{
    public const string CookieName = "notifycenter_admin";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

    public void SignIn(HttpContext context, JwtTokenService jwtTokenService, AdminUser user)
    {
        var token = jwtTokenService.CreateToken(
            user.Username,
            ["notifications.admin"],
            CookieLifetime);

        context.Response.Cookies.Append(
            CookieName,
            token.Token,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = token.ExpiresAtUtc,
                IsEssential = true,
                Path = "/",
                Secure = context.Request.IsHttps
            });
    }

    public void SignOut(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
    }

    public async Task<AdminUser?> GetCurrentUserAsync(
        HttpContext context,
        JwtTokenService jwtTokenService,
        AdminUserRepository repository,
        CancellationToken cancellationToken)
    {
        var token = context.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var principal = jwtTokenService.ValidateToken(token);
        if (principal is null || !principal.HasScope("notifications.admin"))
        {
            return null;
        }

        var user = await repository.GetByUsernameAsync(principal.Subject, cancellationToken);
        if (user is null)
        {
            return null;
        }

        return user.UpdatedAt.ToUnixTimeSeconds() > principal.IssuedAtUnixTimeSeconds
            ? null
            : user;
    }

    public async Task<AuthenticatedPrincipal?> GetCurrentPrincipalAsync(
        HttpContext context,
        JwtTokenService jwtTokenService,
        AdminUserRepository repository,
        CancellationToken cancellationToken)
    {
        var token = context.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var principal = jwtTokenService.ValidateToken(token);
        if (principal is null || !principal.HasScope("notifications.admin"))
        {
            return null;
        }

        var user = await repository.GetByUsernameAsync(principal.Subject, cancellationToken);
        if (user is null)
        {
            return null;
        }

        return user.UpdatedAt.ToUnixTimeSeconds() > principal.IssuedAtUnixTimeSeconds
            ? null
            : principal;
    }
}
