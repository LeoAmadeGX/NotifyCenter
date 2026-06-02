using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Services;

public sealed class NotificationSenderRegistry(TelegramSender telegramSender, LineSender lineSender)
{
    public bool IsSupported(string channel)
    {
        return Normalize(channel) is "telegram" or "line";
    }

    public Task<NotificationSendResult> SendAsync(NotificationItem delivery, CancellationToken cancellationToken)
    {
        return Normalize(delivery.Channel) switch
        {
            "telegram" => telegramSender.SendAsync(delivery, cancellationToken),
            "line" => lineSender.SendAsync(delivery, cancellationToken),
            _ => throw new UnsupportedNotificationChannelException(delivery.Channel)
        };
    }

    public string Normalize(string? channel)
    {
        return string.IsNullOrWhiteSpace(channel) ? "telegram" : channel.Trim().ToLowerInvariant();
    }
}

public sealed class UnsupportedNotificationChannelException(string channel)
    : Exception($"Unsupported notification channel: {channel}")
{
    public string Channel { get; } = channel;
}
