using Npgsql;

namespace NotifyCenter.Api.Configuration;

public sealed class AppOptions
{
    public required string DatabaseConnectionString { get; init; }
    public required JwtOptions Jwt { get; init; }
    public required TelegramOptions Telegram { get; init; }
    public required int PollSeconds { get; init; }

    public static AppOptions Load(IConfiguration configuration)
    {
        var databaseUrl = Required(configuration, "DATABASE_URL");

        return new AppOptions
        {
            DatabaseConnectionString = BuildPostgresConnectionString(databaseUrl),
            Jwt = new JwtOptions
            {
                Issuer = configuration["JWT_ISSUER"] ?? "NotifyCenter",
                Audience = configuration["JWT_AUDIENCE"] ?? "NotifyCenterClients",
                SigningKey = Required(configuration, "JWT_SIGNING_KEY"),
                ExpiresMinutes = ReadInt(configuration, "JWT_EXPIRES_MINUTES", 1440)
            },
            Telegram = new TelegramOptions
            {
                BotToken = configuration["TELEGRAM_BOT_TOKEN"],
                DefaultTarget = configuration["TELEGRAM_DEFAULT_CHAT_ID"],
                ParseMode = configuration["TELEGRAM_PARSE_MODE"] ?? "HTML"
            },
            PollSeconds = Math.Max(5, ReadInt(configuration, "NOTIFICATION_POLL_SECONDS", 30))
        };
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required");
        }

        return value;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        return int.TryParse(configuration[key], out var value) ? value : fallback;
    }

    private static string BuildPostgresConnectionString(string databaseUrl)
    {
        if (!databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return databaseUrl;
        }

        var uri = new Uri(databaseUrl);
        var userParts = uri.UserInfo.Split(':', 2);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = userParts.Length > 0 ? Uri.UnescapeDataString(userParts[0]) : string.Empty,
            Password = userParts.Length > 1 ? Uri.UnescapeDataString(userParts[1]) : string.Empty,
            Pooling = true
        };

        if (query.TryGetValue("schema", out var schema) && !string.IsNullOrWhiteSpace(schema.ToString()))
        {
            builder.SearchPath = schema.ToString();
        }

        if (query.TryGetValue("sslmode", out var sslMode) && !string.IsNullOrWhiteSpace(sslMode.ToString()))
        {
            builder.SslMode = Enum.TryParse<SslMode>(sslMode.ToString(), ignoreCase: true, out var parsed)
                ? parsed
                : SslMode.Prefer;
        }

        return builder.ConnectionString;
    }
}

public sealed class JwtOptions
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKey { get; init; }
    public required int ExpiresMinutes { get; init; }
}

public sealed class TelegramOptions
{
    public string? BotToken { get; init; }
    public string? DefaultTarget { get; init; }
    public string ParseMode { get; init; } = "HTML";
}
