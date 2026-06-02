using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NotifyCenter.Api.Configuration;
using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Services;

public sealed class LineSender(HttpClient httpClient, AppOptions options)
{
    public async Task<NotificationSendResult> SendAsync(NotificationItem delivery, CancellationToken cancellationToken)
    {
        if (!string.Equals(delivery.Channel, "line", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedNotificationChannelException(delivery.Channel);
        }

        if (string.IsNullOrWhiteSpace(options.Line.ChannelAccessToken))
        {
            throw new InvalidOperationException("LINE_CHANNEL_ACCESS_TOKEN is required to send Line notifications");
        }

        if (string.IsNullOrWhiteSpace(delivery.Target))
        {
            throw new InvalidOperationException("Line target userId, groupId, or roomId is required");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push")
        {
            Content = JsonContent.Create(new
            {
                to = delivery.Target,
                messages = new object[]
                {
                    new
                    {
                        type = "text",
                        text = $"{delivery.Title}\n\n{delivery.Body}"
                    }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Line.ChannelAccessToken);

        var response = await httpClient.SendAsync(request, cancellationToken);
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
        var message = TryExtractMessage(responseBody);
        return string.IsNullOrWhiteSpace(message)
            ? "Line push message failed"
            : $"Line push message failed: {message}";
    }

    private static string? TryExtractMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
