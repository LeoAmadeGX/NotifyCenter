using NotifyCenter.Api.Configuration;
using NotifyCenter.Api.Data;

namespace NotifyCenter.Api.Services;

public sealed class NotificationDispatcher(
    NotificationDeliveryRepository deliveryRepository,
    NotificationSenderRegistry senderRegistry,
    AppOptions options,
    AdminDashboardEventBroadcaster eventBroadcaster,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification dispatcher started with {PollSeconds}s polling", options.PollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dueDeliveries = await deliveryRepository.GetDuePendingAsync(DateTimeOffset.UtcNow, 25, stoppingToken);
                foreach (var delivery in dueDeliveries)
                {
                    var effectiveDelivery = delivery.IsTargetOverride
                        ? delivery
                        : await deliveryRepository.RefreshResolvedTargetAsync(
                            delivery.Id,
                            "This routing target is no longer enabled or available at send time, so the delivery was skipped.",
                            stoppingToken) ?? delivery;

                    if (string.Equals(effectiveDelivery.Status, "skipped_no_target", StringComparison.OrdinalIgnoreCase))
                    {
                        eventBroadcaster.Publish("deliveries_changed", effectiveDelivery.Id, effectiveDelivery.Channel);
                        logger.LogInformation(
                            "Delivery {DeliveryId} skipped because its routing target is no longer available",
                            effectiveDelivery.Id);
                        continue;
                    }

                    if (string.Equals(effectiveDelivery.Status, "pending_no_target", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(effectiveDelivery.Target))
                    {
                        await deliveryRepository.MarkSkippedNoTargetAsync(effectiveDelivery, stoppingToken);
                        eventBroadcaster.Publish("deliveries_changed", effectiveDelivery.Id, effectiveDelivery.Channel);
                        logger.LogInformation(
                            "Delivery {DeliveryId} skipped because no enabled routing targets were available",
                            effectiveDelivery.Id);
                        continue;
                    }

                    try
                    {
                        var result = await senderRegistry.SendAsync(effectiveDelivery, stoppingToken);
                        await deliveryRepository.MarkSentAsync(effectiveDelivery, result.HttpStatus, result.ResponseBody, stoppingToken);
                        eventBroadcaster.Publish("deliveries_changed", effectiveDelivery.Id, effectiveDelivery.Channel);
                        logger.LogInformation("Delivery {DeliveryId} sent via {Channel}", effectiveDelivery.Id, effectiveDelivery.Channel);
                    }
                    catch (NotificationSendException ex)
                    {
                        await deliveryRepository.MarkFailedAsync(
                            effectiveDelivery,
                            ex.HttpStatus,
                            ex.ResponseBody,
                            ex.Message,
                            stoppingToken);
                        eventBroadcaster.Publish("deliveries_changed", effectiveDelivery.Id, effectiveDelivery.Channel);
                        logger.LogWarning(ex, "Delivery {DeliveryId} failed", effectiveDelivery.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await deliveryRepository.MarkFailedAsync(
                            effectiveDelivery,
                            null,
                            null,
                            ex.Message,
                            stoppingToken);
                        eventBroadcaster.Publish("deliveries_changed", effectiveDelivery.Id, effectiveDelivery.Channel);
                        logger.LogWarning(ex, "Delivery {DeliveryId} failed", effectiveDelivery.Id);
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
