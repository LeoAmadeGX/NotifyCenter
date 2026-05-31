using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotifyCenter.Api.Configuration;

namespace NotifyCenter.Api.Auth;

public sealed class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly byte[] _signingKey;

    public JwtTokenService(AppOptions options)
    {
        _options = options.Jwt;
        if (_options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("JWT_SIGNING_KEY must be at least 32 characters");
        }

        _signingKey = Encoding.UTF8.GetBytes(_options.SigningKey);
    }

    public (string Token, DateTimeOffset ExpiresAtUtc) CreateToken(
        string subject,
        IEnumerable<string> scopes,
        TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(_options.ExpiresMinutes));
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = _options.Issuer,
            aud = _options.Audience,
            sub = subject,
            scope = string.Join(' ', scopes.Distinct(StringComparer.Ordinal)),
            iat = now.ToUnixTimeSeconds(),
            nbf = now.ToUnixTimeSeconds(),
            exp = expires.ToUnixTimeSeconds()
        }));

        var signed = $"{header}.{payload}";
        var signature = Sign(signed);
        return ($"{signed}.{signature}", expires);
    }

    public AuthenticatedPrincipal? ValidateToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var signed = $"{parts[0]}.{parts[1]}";
        var expected = Sign(signed);
        if (!FixedEquals(expected, parts[2]))
        {
            return null;
        }

        JwtPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<JwtPayload>(Base64UrlDecode(parts[1]));
        }
        catch
        {
            return null;
        }

        if (payload is null ||
            payload.Issuer != _options.Issuer ||
            payload.Audience != _options.Audience ||
            string.IsNullOrWhiteSpace(payload.Subject))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long clockSkewSeconds = 60;
        if (payload.NotBefore > now + clockSkewSeconds || payload.ExpiresAt <= now - clockSkewSeconds)
        {
            return null;
        }

        var scopes = (payload.Scope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return new AuthenticatedPrincipal(payload.Subject, scopes);
    }

    private string Sign(string value)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length")
        };
        return Convert.FromBase64String(padded);
    }
}

public sealed record AuthenticatedPrincipal(string Subject, IReadOnlySet<string> Scopes)
{
    public bool HasScope(string scope)
    {
        return Scopes.Contains(scope) || Scopes.Contains("notifications.admin");
    }
}

internal sealed class JwtPayload
{
    [JsonPropertyName("iss")]
    public string Issuer { get; init; } = string.Empty;

    [JsonPropertyName("aud")]
    public string Audience { get; init; } = string.Empty;

    [JsonPropertyName("sub")]
    public string Subject { get; init; } = string.Empty;

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("nbf")]
    public long NotBefore { get; init; }

    [JsonPropertyName("exp")]
    public long ExpiresAt { get; init; }
}
