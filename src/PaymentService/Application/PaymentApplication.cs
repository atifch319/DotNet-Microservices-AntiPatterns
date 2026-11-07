using Commerce.Contracts.Hosting;
using Commerce.Payments.Data;
using Commerce.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Payments.Application;

public sealed record AuthorizationResult(PaymentRecord Payment, bool Created);

public sealed class PaymentApplication(PaymentsDbContext db)
{
    public async Task<PaymentRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await db.Payments.AsNoTracking().SingleOrDefaultAsync(payment => payment.Id == id, cancellationToken);
    }

    public async Task<AuthorizationResult> AuthorizeAsync(
        Guid orderId,
        decimal amount,
        string simulation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["orderId"] = ["OrderId is required."] });
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 80)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["A key of 1 to 80 characters is required."] });
        }

        var existing = await db.Payments.SingleOrDefaultAsync(
            payment => payment.IdempotencyKey == idempotencyKey || payment.OrderId == orderId,
            cancellationToken);
        if (existing is not null)
        {
            return new AuthorizationResult(existing, false);
        }

        var decision = SimulatedPaymentGateway.Decide(simulation, amount);
        if (decision.IsValidationError)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["simulation"] = [decision.Reason ?? "Invalid simulation."] });
        }

        var payment = new PaymentRecord
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Amount = amount,
            Status = decision.Authorized ? PaymentStatus.Authorized : PaymentStatus.Declined,
            IdempotencyKey = idempotencyKey,
            Reason = decision.Reason,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Payments.Add(payment);
        return new AuthorizationResult(payment, true);
    }
}
