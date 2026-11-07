using Commerce.Contracts.Events;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Inventory.Application;
using Commerce.Inventory.Domain;

namespace Commerce.Inventory.Messaging;

public sealed class InventoryMessageHandler(InventoryApplication inventory, Inventory.Data.InventoryDbContext db, ILogger<InventoryMessageHandler> logger) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        await InboxTransaction.RunAsync(db, ConsumerNames.Inventory, envelope, async ct =>
        {
            switch (envelope.EventType)
            {
                case EventTypes.ReserveInventory:
                    await ReserveAsync(envelope, ct);
                    break;
                case EventTypes.ReleaseInventory:
                    await ReleaseAsync(envelope, ct);
                    break;
                default:
                    throw new InvalidOperationException($"Inventory consumer does not handle {envelope.EventType}.");
            }
        }, cancellationToken);
    }

    private async Task ReserveAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = IntegrationJson.DeserializePayload<ReserveInventoryV1>(envelope.Payload);
        var outcome = await inventory.ReserveAsync(
            command.OrderId,
            command.Lines.Select(line => new ReservationLineRequest(line.ProductId, line.Quantity)).ToList(),
            cancellationToken);

        switch (outcome)
        {
            case ReservationOutcome.Reserved reserved:
                OutboxWriter.Add(db, EventTypes.InventoryReserved, 1, command.OrderId, envelope.EventId, new InventoryReservedV1(command.OrderId, reserved.ReservationId));
                logger.LogInformation("Reserved inventory {ReservationId} for order {OrderId}", reserved.ReservationId, command.OrderId);
                break;
            case ReservationOutcome.Rejected rejected:
                OutboxWriter.Add(db, EventTypes.InventoryReservationFailed, 1, command.OrderId, envelope.EventId, new InventoryReservationFailedV1(command.OrderId, rejected.Reason));
                logger.LogInformation("Rejected inventory for order {OrderId}: {Reason}", command.OrderId, rejected.Reason);
                break;
        }
    }

    private async Task ReleaseAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = IntegrationJson.DeserializePayload<ReleaseInventoryV1>(envelope.Payload);
        var outcome = await inventory.ReleaseByOrderAsync(command.OrderId, command.ReservationId, cancellationToken);
        switch (outcome)
        {
            case ReleaseOutcome.Released released:
                OutboxWriter.Add(db, EventTypes.InventoryReleased, 1, command.OrderId, envelope.EventId, new InventoryReleasedV1(command.OrderId, released.ReservationId));
                logger.LogInformation("Released reservation {ReservationId} for order {OrderId}", released.ReservationId, command.OrderId);
                break;
            case ReleaseOutcome.Failed failed:
                OutboxWriter.Add(db, EventTypes.InventoryReleaseFailed, 1, command.OrderId, envelope.EventId, new InventoryReleaseFailedV1(command.OrderId, command.ReservationId, failed.Reason));
                logger.LogWarning("Inventory release failed for order {OrderId}: {Reason}", command.OrderId, failed.Reason);
                break;
            case ReleaseOutcome.Missing:
                OutboxWriter.Add(db, EventTypes.InventoryReleaseFailed, 1, command.OrderId, envelope.EventId, new InventoryReleaseFailedV1(command.OrderId, command.ReservationId, "Reservation was not found."));
                break;
        }
    }
}
