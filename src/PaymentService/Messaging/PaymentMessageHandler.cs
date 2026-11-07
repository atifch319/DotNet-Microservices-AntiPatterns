using Commerce.Contracts.Events;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Payments.Application;
using Commerce.Payments.Domain;

namespace Commerce.Payments.Messaging;

public sealed class PaymentMessageHandler(PaymentApplication payments, Payments.Data.PaymentsDbContext db, ILogger<PaymentMessageHandler> logger) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.EventType != EventTypes.AuthorizePayment)
        {
            throw new InvalidOperationException($"Payment consumer does not handle {envelope.EventType}.");
        }

        await InboxTransaction.RunAsync(db, ConsumerNames.Payment, envelope, async ct =>
        {
            var command = IntegrationJson.DeserializePayload<AuthorizePaymentV1>(envelope.Payload);
            var result = await payments.AuthorizeAsync(command.OrderId, command.Amount, command.Simulation, $"saga:{command.OrderId:N}", ct);
            if (!result.Created)
            {
                logger.LogInformation("Payment for order {OrderId} already exists. No second side effect was recorded.", command.OrderId);
                return;
            }

            if (result.Payment.Status == PaymentStatus.Authorized)
            {
                OutboxWriter.Add(db, EventTypes.PaymentAuthorized, 1, command.OrderId, envelope.EventId, new PaymentAuthorizedV1(command.OrderId, result.Payment.Id, result.Payment.Amount));
                logger.LogInformation("Authorized simulated payment {PaymentId} for order {OrderId}", result.Payment.Id, command.OrderId);
                return;
            }

            OutboxWriter.Add(db, EventTypes.PaymentFailed, 1, command.OrderId, envelope.EventId, new PaymentFailedV1(command.OrderId, result.Payment.Reason ?? "Payment was declined."));
            logger.LogInformation("Declined simulated payment for order {OrderId}", command.OrderId);
        }, cancellationToken);
    }
}
