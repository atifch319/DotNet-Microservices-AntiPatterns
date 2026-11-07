using Commerce.Payments.Domain;

namespace PaymentService.Tests;

public class SimulatedPaymentGatewayTests
{
    [Fact]
    public void Authorize_simulation_approves_an_amount_inside_the_limit()
    {
        var decision = SimulatedPaymentGateway.Decide("authorize", 49.90m);
        Assert.True(decision.Authorized);
        Assert.False(decision.IsValidationError);
    }

    [Fact]
    public void Decline_simulation_does_not_authorize()
    {
        var decision = SimulatedPaymentGateway.Decide("decline", 10m);
        Assert.False(decision.Authorized);
        Assert.False(decision.IsValidationError);
    }

    [Fact]
    public void Amounts_above_the_limit_are_declined()
    {
        var decision = SimulatedPaymentGateway.Decide("authorize", SimulatedPaymentGateway.AuthorizationLimit + 1);
        Assert.False(decision.Authorized);
    }

    [Fact]
    public void Card_like_input_is_rejected_as_validation()
    {
        var decision = SimulatedPaymentGateway.Decide("4111111111111111", 10m);
        Assert.True(decision.IsValidationError);
        Assert.False(decision.Authorized);
    }
}
