using NotifyCenter.Api.Configuration;
using NotifyCenter.Api.Data;

namespace NotifyCenter.Api.Services;

public sealed class NotificationDispatcher(
    NotificationRepository repository,
    NotificationSenderRegistry senderRegistry,
    AppOptions options,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification dispatcher started with {PollSeconds}s polling", options.PollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dueItems = await repository.GetDuePendingAsync(DateTimeOffset.UtcNow, 25, stoppingToken);
                foreach (var item in dueItems)
                {
                    try
                    {
                        var result = await senderRegistry.SendAsync(item, stoppingToken);
                        await repository.MarkSentAsync(item.Id, result.HttpStatus, result.ResponseBody, stoppingToken);
                        logger.LogInformation("Notification {NotificationId} sent via {Channel}", item.Id, item.Channel);
                    }
                    catch (NotificationSendException ex)
                    {
                        await repository.MarkFailedAsync(
                            item.Id,
                            ex.HttpStatus,
                            ex.ResponseBody,
                            ex.Message,
                            stoppingToken);
                        logger.LogWarning(ex, "Notification {NotificationId} failed", item.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await repository.MarkFailedAsync(
                            item.Id,
                            null,
                            null,
                            ex.Message,
                            stoppingToken);
                        logger.LogWarning(ex, "Notification {NotificationId} failed", item.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification dispatcher loop failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
        }
    }
}
