using System.Net.Http.Json;
using System.Text.Json;
using NotifyCenter.Api.Configuration;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Services;

public sealed class TelegramSender(HttpClient httpClient, AppOptions options)
{
    public async Task<NotificationSendResult> SendAsync(NotificationItem delivery, CancellationToken cancellationToken)
    {
        if (!string.Equals(delivery.Channel, "telegram", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedNotificationChannelException(delivery.Channel);
        }

        if (string.IsNullOrWhiteSpace(options.Telegram.BotToken))
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is required to send Telegram notifications");
        }

        if (string.IsNullOrWhiteSpace(delivery.Target))
        {
            throw new InvalidOperationException("Telegram target chat id is required");
        }

        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = delivery.Target,
            ["text"] = $"{delivery.Title}\n\n{delivery.Body}",
            ["disable_web_page_preview"] = true
        };

        if (!string.IsNullOrWhiteSpace(options.Telegram.ParseMode))
        {
            payload["parse_mode"] = options.Telegram.ParseMode;
        }

        var response = await httpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{options.Telegram.BotToken}/sendMessage",
            payload,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationSendException(
                (int)response.StatusCode,
                body,
                BuildErrorMessage(body));
        }

        return new NotificationSendResult((int)response.StatusCode, body);
    }

    private static string BuildErrorMessage(string responseBody)
    {
        var description = TryExtractDescription(responseBody);
        return string.IsNullOrWhiteSpace(description)
            ? "Telegram sendMessage failed"
            : $"Telegram sendMessage failed: {description}";
    }

    private static string? TryExtractDescription(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("description", out var description) &&
                description.ValueKind == JsonValueKind.String)
            {
                return description.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

public sealed record NotificationSendResult(int HttpStatus, string ResponseBody);

public sealed class NotificationSendException(int? httpStatus, string? responseBody, string message)
    : Exception(httpStatus is null ? message : $"{message} with HTTP {httpStatus}")
{
    public int? HttpStatus { get; } = httpStatus;
    public string? ResponseBody { get; } = responseBody;
}
