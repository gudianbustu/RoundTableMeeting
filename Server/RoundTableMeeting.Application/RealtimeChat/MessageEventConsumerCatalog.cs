using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Application.RealtimeChat;

public sealed class MessageEventConsumerCatalog : IMessageEventConsumerCatalog, ISingletonDependency<IMessageEventConsumerCatalog>
{
    private readonly IReadOnlyDictionary<string, Type> consumers;

    public MessageEventConsumerCatalog(IEnumerable<MessageEventConsumerDescriptor>? descriptors = null)
    {
        var entries = (descriptors ?? []).ToArray();
        if (entries.Any(x => string.IsNullOrWhiteSpace(x.ConsumerName) || !x.ConsumerName.Contains("-v", StringComparison.Ordinal)))
            throw new InvalidOperationException("Message event consumer names must be versioned and non-empty.");
        if (entries.GroupBy(x => x.ConsumerName, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Message event consumer names must be unique.");
        if (entries.Any(x => !typeof(IMessageEventConsumer).IsAssignableFrom(x.ServiceType)))
            throw new InvalidOperationException("Message event consumers must implement IMessageEventConsumer.");
        consumers = entries.ToDictionary(x => x.ConsumerName, x => x.ServiceType, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> ConsumerNames => consumers.Keys.ToArray();
    public Type GetRequiredServiceType(string consumerName) => consumers.TryGetValue(consumerName, out var serviceType)
        ? serviceType
        : throw new InvalidOperationException($"Unknown message event consumer: {consumerName}.");
}
