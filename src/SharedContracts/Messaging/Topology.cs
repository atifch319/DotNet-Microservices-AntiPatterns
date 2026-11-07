using RabbitMQ.Client;

namespace Commerce.Contracts.Messaging;

public sealed record QueueBinding(string QueueName, IReadOnlyList<string> RoutingKeys);

public static class Topology
{
    public const string Exchange = "commerce.events";
    public const string DeadLetterExchange = "commerce.dlx";

    public const string InventoryQueue = "q.inventory.commands";
    public const string PaymentQueue = "q.payment.commands";
    public const string OrderQueue = "q.order.workflow";
    public const string NotificationQueue = "q.notification.orders";

    public static readonly IReadOnlyList<QueueBinding> Queues =
    [
        new(InventoryQueue, [Events.EventTypes.ReserveInventory, Events.EventTypes.ReleaseInventory]),
        new(PaymentQueue, [Events.EventTypes.AuthorizePayment]),
        new(OrderQueue,
        [
            Events.EventTypes.InventoryReserved,
            Events.EventTypes.InventoryReservationFailed,
            Events.EventTypes.InventoryReleased,
            Events.EventTypes.InventoryReleaseFailed,
            Events.EventTypes.PaymentAuthorized,
            Events.EventTypes.PaymentFailed
        ]),
        new(NotificationQueue,
        [
            Events.EventTypes.OrderPlaced,
            Events.EventTypes.OrderConfirmed,
            Events.EventTypes.OrderFailed,
            Events.EventTypes.OrderCompensationFailed
        ])
    ];

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        foreach (var queue in Queues)
        {
            var deadLetterQueue = queue.QueueName + ".dlq";
            var arguments = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = deadLetterQueue
            };

            await channel.QueueDeclareAsync(queue.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: cancellationToken);
            await channel.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(deadLetterQueue, DeadLetterExchange, deadLetterQueue, cancellationToken: cancellationToken);

            foreach (var routingKey in queue.RoutingKeys)
            {
                await channel.QueueBindAsync(queue.QueueName, Exchange, routingKey, cancellationToken: cancellationToken);
            }
        }
    }
}
