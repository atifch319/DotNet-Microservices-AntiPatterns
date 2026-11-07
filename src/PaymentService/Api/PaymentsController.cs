using Commerce.Payments.Application;
using Commerce.Payments.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Commerce.Payments.Api;

[ApiController]
public sealed class PaymentsController(PaymentApplication payments, Payments.Data.PaymentsDbContext db) : ControllerBase
{
    [HttpPost("/api/payments/authorize")]
    public async Task<ActionResult<PaymentResponse>> Authorize(
        [FromBody] AuthorizePaymentRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        request ??= new AuthorizePaymentRequest();
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? request.IdempotencyKey : idempotencyKey;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await payments.AuthorizeAsync(request.OrderId, request.Amount, request.Simulation, key ?? "", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var body = PaymentResponse.From(result.Payment);
        return result.Created
            ? StatusCode(result.Payment.Status == PaymentStatus.Authorized ? StatusCodes.Status201Created : StatusCodes.Status422UnprocessableEntity, body)
            : Ok(body);
    }

    [HttpGet("/api/payments/{id:guid}")]
    public async Task<ActionResult<PaymentResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(id, cancellationToken);
        return payment is null ? NotFound() : Ok(PaymentResponse.From(payment));
    }
}

public sealed class AuthorizePaymentRequest
{
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Simulation { get; set; } = "";
    public string? IdempotencyKey { get; set; }
}

public sealed record PaymentResponse(Guid Id, Guid OrderId, decimal Amount, string Status, string? Reason, string IdempotencyKey)
{
    public static PaymentResponse From(PaymentRecord payment) =>
        new(payment.Id, payment.OrderId, payment.Amount, payment.Status, payment.Reason, payment.IdempotencyKey);
}
