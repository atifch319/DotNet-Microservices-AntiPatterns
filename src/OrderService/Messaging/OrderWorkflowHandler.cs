using Commerce.Contracts.Events;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Orders.Application;
using Commerce.Orders.Data;
using Commerce.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Orders.Messaging;

public sealed class OrderWorkflowHandler(OrdersDbContext db, ILogger<OrderWorkflowHandler> logger) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        await InboxTransaction.RunAsync(db, ConsumerNames.OrderWorkflow, envelope, async ct =>
        {
            var order = await db.Orders
                .Include(row => row.Items)
                .Include(row => row.Transitions)
                .SingleOrDefaultAsync(row => row.Id == envelope.CorrelationId, ct);

            if (order is null)
            {
                throw new InvalidOperationException($"Order {envelope.CorrelationId} was not found for event {envelope.EventId}.");
            }

            var now = DateTimeOffset.UtcNow;
            var decision = Apply(order, envelope, now);
            if (decision.Result == WorkflowApplicationResult.Applied)
            {
                OrderCommands.Write(db, order, decision.Commands, envelope.EventId);
                logger.LogInformation("Order {OrderId} moved to {Status} after {EventType}", order.Id, order.Status, envelope.EventType);
                return;
            }

            db.WorkflowIncidents.Add(new WorkflowIncident
            {
                OrderId = order.Id,
                EventId = envelope.EventId,
                EventType = envelope.EventType,
                Outcome = decision.Result.ToString(),
                Detail = decision.Detail,
                OccurredAt = now
            });
            logger.LogInformation(
                "Order {OrderId} did not apply {EventType}. Outcome {Outcome}. {Detail}",
                order.Id,
                envelope.EventType,
                decision.Result,
                decision.Detail);
        }, cancellationToken);
    }

    private static WorkflowDecision Apply(Order order, IntegrationEventEnvelope envelope, DateTimeOffset now)
    {
        return envelope.EventType switch
        {
            EventTypes.InventoryReserved => With<InventoryReservedV1>(envelope, payload =>
                OrderWorkflow.OnInventoryReserved(order, envelope.EventId, payload.ReservationId, now)),
            EventTypes.InventoryReservationFailed => With<InventoryReservationFailedV1>(envelope, payload =>
                OrderWorkflow.OnInventoryReservationFailed(order, envelope.EventId, payload.Reason, now)),
            EventTypes.PaymentAuthorized => With<PaymentAuthorizedV1>(envelope, payload =>
                OrderWorkflow.OnPaymentAuthorized(order, envelope.EventId, payload.PaymentId, now)),
            EventTypes.PaymentFailed => With<PaymentFailedV1>(envelope, payload =>
                OrderWorkflow.OnPaymentFailed(order, envelope.EventId, payload.Reason, now)),
            EventTypes.InventoryReleased => With<InventoryReleasedV1>(envelope, payload =>
                OrderWorkflow.OnInventoryReleased(order, envelope.EventId, payload.ReservationId, now)),
            EventTypes.InventoryReleaseFailed => With<InventoryReleaseFailedV1>(envelope, payload =>
                OrderWorkflow.OnInventoryReleaseFailed(order, envelope.EventId, payload.Reason, now)),
            _ => WorkflowDecision.Late($"Event type {envelope.EventType} is not a workflow result.")
        };
    }

    private static WorkflowDecision With<TPayload>(IntegrationEventEnvelope envelope, Func<TPayload, WorkflowDecision> apply)
    {
        var payload = IntegrationJson.DeserializePayload<TPayload>(envelope.Payload);
        return apply(payload);
    }
}
