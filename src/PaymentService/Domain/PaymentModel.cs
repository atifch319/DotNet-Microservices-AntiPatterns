namespace Commerce.Payments.Domain;

public static class PaymentStatus
{
    public const string Authorized = "Authorized";
    public const string Declined = "Declined";
}

public sealed class PaymentRecord
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public readonly record struct PaymentDecision(bool IsValidationError, bool Authorized, string? Reason)
{
    public static PaymentDecision Valid(bool authorized, string? reason) => new(false, authorized, reason);
    public static PaymentDecision Invalid(string reason) => new(true, false, reason);
}

public static class SimulatedPaymentGateway
{
    public const decimal AuthorizationLimit = 10_000m;

    public static PaymentDecision Decide(string? simulation, decimal amount)
    {
        var normalized = simulation?.Trim().ToLowerInvariant();
        if (normalized is not ("authorize" or "decline"))
        {
            return PaymentDecision.Invalid("Simulation must be authorize or decline. Do not send card data.");
        }

        if (amount <= 0)
        {
            return PaymentDecision.Invalid("Amount must be greater than zero.");
        }

        if (normalized == "decline")
        {
            return PaymentDecision.Valid(false, "The simulated payment was declined.");
        }

        if (amount > AuthorizationLimit)
        {
            return PaymentDecision.Valid(false, "The amount exceeds the simulated authorization limit of 10000.");
        }

        return PaymentDecision.Valid(true, null);
    }
}
