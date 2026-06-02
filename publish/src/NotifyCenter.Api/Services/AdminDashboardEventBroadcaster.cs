using System.Collections.Concurrent;
using System.Threading.Channels;

namespace NotifyCenter.Api.Services;

public sealed record AdminDashboardEvent(
    string Kind,
    Guid? DeliveryId,
    string? Channel,
    DateTimeOffset OccurredAt);

public sealed class AdminDashboardEventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<AdminDashboardEvent>> subscribers = new();

    public EventSubscription Subscribe(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<AdminDashboardEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        subscribers[id] = channel;
        var registration = cancellationToken.Register(() => RemoveSubscriber(id));
        return new EventSubscription(id, channel.Reader, registration, RemoveSubscriber);
    }

    public void Publish(string kind, Guid? deliveryId = null, string? channel = null)
    {
        var @event = new AdminDashboardEvent(kind, deliveryId, channel, DateTimeOffset.UtcNow);

        foreach (var subscriber in subscribers.Values)
        {
            subscriber.Writer.TryWrite(@event);
        }
    }

    private void RemoveSubscriber(Guid id)
    {
        if (subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public sealed class EventSubscription(
        Guid id,
        ChannelReader<AdminDashboardEvent> reader,
        CancellationTokenRegistration registration,
        Action<Guid> removeSubscriber) : IAsyncDisposable
    {
        public ChannelReader<AdminDashboardEvent> Reader { get; } = reader;

        public ValueTask DisposeAsync()
        {
            registration.Dispose();
            removeSubscriber(id);
            return ValueTask.CompletedTask;
        }
    }
}
