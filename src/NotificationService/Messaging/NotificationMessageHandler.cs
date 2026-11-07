using Commerce.Contracts.Events;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Notifications.Data;

namespace Commerce.Notifications.Messaging;

public sealed class NotificationMessageHandler(NotificationsDbContext db, ILogger<NotificationMessageHandler> logger) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var template = envelope.EventType switch
        {
            EventTypes.OrderPlaced => "OrderReceived",
            EventTypes.OrderConfirmed => "OrderConfirmed",
            EventTypes.OrderFailed => "OrderFailed",
            EventTypes.OrderCompensationFailed => "CompensationFailed",
            _ => throw new InvalidOperationException($"Notification consumer does not handle {envelope.EventType}.")
        };

        await InboxTransaction.RunAsync(db, ConsumerNames.Notification, envelope, _ =>
        {
            db.Notifications.Add(new NotificationRecord
            {
                Id = Guid.NewGuid(),
                OrderId = envelope.CorrelationId,
                Channel = "SimulatedEmail",
                Status = "Delivered",
                Template = template,
                SourceEventId = envelope.EventId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            logger.LogInformation("Recorded simulated {Template} notification for order {OrderId}", template, envelope.CorrelationId);
            return Task.CompletedTask;
        }, cancellationToken);
    }
}
