using NotifyCenter.Api.Models;

namespace NotifyCenter.Api.Services;

public sealed class NotificationSenderRegistry(TelegramSender telegramSender)
{
    public bool IsSupported(string channel)
    {
        return string.Equals(channel, "telegram", StringComparison.OrdinalIgnoreCase);
    }

    public Task<NotificationSendResult> SendAsync(NotificationItem item, CancellationToken cancellationToken)
    {
        return Normalize(item.Channel) switch
        {
            "telegram" => telegramSender.SendAsync(item, cancellationToken),
            _ => throw new UnsupportedNotificationChannelException(item.Channel)
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
