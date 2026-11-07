using Commerce.Orders.Domain;

namespace OrderService.Tests;

public class OrderWorkflowTests
{
    [Fact]
    public void Successful_order_reaches_confirmed_and_records_payment_authorization()
    {
        var now = DateTimeOffset.UtcNow;
        var order = PendingOrder();
        var reservationId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        var reserved = OrderWorkflow.OnInventoryReserved(order, Guid.NewGuid(), reservationId, now);
        var paid = OrderWorkflow.OnPaymentAuthorized(order, Guid.NewGuid(), paymentId, now.AddSeconds(1));

        Assert.Equal(WorkflowApplicationResult.Applied, reserved.Result);
        Assert.IsType<RequestPaymentAuthorization>(Assert.Single(reserved.Commands));
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(paymentId, order.PaymentId);
        Assert.Contains(order.Transitions, transition => transition.ToStatus == OrderStatus.PaymentAuthorized);
        Assert.Contains(order.Transitions, transition => transition.ToStatus == OrderStatus.Confirmed);
        Assert.IsType<NotifyOrderConfirmed>(Assert.Single(paid.Commands));
    }

    [Fact]
    public void Inventory_rejection_fails_the_order_without_compensation()
    {
        var order = PendingOrder();
        var decision = OrderWorkflow.OnInventoryReservationFailed(order, Guid.NewGuid(), "Insufficient available quantity.", DateTimeOffset.UtcNow);

        Assert.Equal(OrderStatus.Failed, order.Status);
        Assert.IsType<NotifyOrderFailed>(Assert.Single(decision.Commands));
        Assert.DoesNotContain(decision.Commands, command => command is RequestInventoryRelease);
    }

    [Fact]
    public void Payment_rejection_requests_release_and_does_not_fail_before_compensation()
    {
        var order = ReservedOrder();
        var decision = OrderWorkflow.OnPaymentFailed(order, Guid.NewGuid(), "The simulated payment was declined.", DateTimeOffset.UtcNow);

        Assert.Equal(OrderStatus.CompensationPending, order.Status);
        Assert.IsType<RequestInventoryRelease>(Assert.Single(decision.Commands));
        Assert.NotEqual(OrderStatus.Failed, order.Status);
    }

    [Fact]
    public void Inventory_release_completes_compensation_as_failed()
    {
        var order = ReservedOrder();
        OrderWorkflow.OnPaymentFailed(order, Guid.NewGuid(), "declined", DateTimeOffset.UtcNow);
        var released = OrderWorkflow.OnInventoryReleased(order, Guid.NewGuid(), order.ReservationId!.Value, DateTimeOffset.UtcNow);

        Assert.Equal(OrderStatus.Failed, order.Status);
        Assert.IsType<NotifyOrderFailed>(Assert.Single(released.Commands));
    }

    [Fact]
    public void Compensation_failure_is_not_marked_failed()
    {
        var order = ReservedOrder();
        OrderWorkflow.OnPaymentFailed(order, Guid.NewGuid(), "declined", DateTimeOffset.UtcNow);
        var decision = OrderWorkflow.OnInventoryReleaseFailed(order, Guid.NewGuid(), "Manual intervention is required.", DateTimeOffset.UtcNow);

        Assert.Equal(OrderStatus.CompensationFailed, order.Status);
        Assert.NotEqual(OrderStatus.Failed, order.Status);
        Assert.IsType<NotifyCompensationFailed>(Assert.Single(decision.Commands));
    }

    [Fact]
    public void A_later_release_can_move_compensation_failure_to_failed()
    {
        var order = ReservedOrder();
        var reservationId = order.ReservationId!.Value;
        OrderWorkflow.OnPaymentFailed(order, Guid.NewGuid(), "declined", DateTimeOffset.UtcNow);
        OrderWorkflow.OnInventoryReleaseFailed(order, Guid.NewGuid(), "simulated", DateTimeOffset.UtcNow);

        var recovered = OrderWorkflow.OnInventoryReleased(order, Guid.NewGuid(), reservationId, DateTimeOffset.UtcNow);

        Assert.Equal(OrderStatus.Failed, order.Status);
        Assert.Equal(WorkflowApplicationResult.Applied, recovered.Result);
    }

    [Fact]
    public void Duplicate_payment_event_does_not_emit_another_command()
    {
        var order = PendingOrder();
        var reservationId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        OrderWorkflow.OnInventoryReserved(order, Guid.NewGuid(), reservationId, DateTimeOffset.UtcNow);
        var first = OrderWorkflow.OnPaymentAuthorized(order, Guid.NewGuid(), paymentId, DateTimeOffset.UtcNow);
        var transitions = order.Transitions.Count;

        var duplicate = OrderWorkflow.OnPaymentAuthorized(order, Guid.NewGuid(), paymentId, DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.Applied, first.Result);
        Assert.Equal(WorkflowApplicationResult.Duplicate, duplicate.Result);
        Assert.Empty(duplicate.Commands);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(transitions, order.Transitions.Count);
    }

    [Fact]
    public void Duplicate_inventory_event_does_not_request_payment_again()
    {
        var order = PendingOrder();
        var reservationId = Guid.NewGuid();
        var first = OrderWorkflow.OnInventoryReserved(order, Guid.NewGuid(), reservationId, DateTimeOffset.UtcNow);
        var duplicate = OrderWorkflow.OnInventoryReserved(order, Guid.NewGuid(), reservationId, DateTimeOffset.UtcNow);

        Assert.Single(first.Commands);
        Assert.Equal(WorkflowApplicationResult.Duplicate, duplicate.Result);
        Assert.Empty(duplicate.Commands);
    }

    [Fact]
    public void Duplicate_payment_failure_does_not_request_a_second_release()
    {
        var order = ReservedOrder();
        OrderWorkflow.OnPaymentFailed(order, Guid.NewGuid(), "declined", DateTimeOffset.UtcNow);
        var duplicate = OrderWorkflow.OnPaymentFailed(order, Guid.NewGuid(), "declined", DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.Duplicate, duplicate.Result);
        Assert.Empty(duplicate.Commands);
        Assert.Equal(OrderStatus.CompensationPending, order.Status);
    }

    [Fact]
    public void Payment_authorized_before_inventory_is_ignored()
    {
        var order = PendingOrder();
        var decision = OrderWorkflow.OnPaymentAuthorized(order, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.IgnoredLate, decision.Result);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Empty(decision.Commands);
        Assert.Null(order.PaymentId);
    }

    [Fact]
    public void Inventory_reserved_after_failure_does_not_revive_the_order()
    {
        var order = PendingOrder();
        OrderWorkflow.OnInventoryReservationFailed(order, Guid.NewGuid(), "none left", DateTimeOffset.UtcNow);
        var late = OrderWorkflow.OnInventoryReserved(order, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.IgnoredLate, late.Result);
        Assert.Equal(OrderStatus.Failed, order.Status);
    }

    [Fact]
    public void Illegal_transitions_are_rejected()
    {
        Assert.False(OrderWorkflow.IsLegal(OrderStatus.Pending, OrderStatus.Confirmed));
        Assert.False(OrderWorkflow.IsLegal(OrderStatus.Pending, OrderStatus.PaymentAuthorized));
        Assert.False(OrderWorkflow.IsLegal(OrderStatus.Confirmed, OrderStatus.Failed));
        Assert.False(OrderWorkflow.IsLegal(OrderStatus.Failed, OrderStatus.Confirmed));
        Assert.True(OrderWorkflow.IsLegal(OrderStatus.Pending, OrderStatus.InventoryReserved));
        Assert.True(OrderWorkflow.IsLegal(OrderStatus.CompensationPending, OrderStatus.CompensationFailed));
        Assert.True(OrderWorkflow.IsLegal(OrderStatus.CompensationFailed, OrderStatus.CompensationPending));
        Assert.True(OrderWorkflow.IsLegal(OrderStatus.CompensationFailed, OrderStatus.Failed));
    }

    [Fact]
    public void Retry_from_compensation_failed_requests_one_new_release()
    {
        var order = CompensationFailedOrder();
        var before = order.Transitions.Count;

        var decision = OrderWorkflow.OnCompensationRetry(order, DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.Applied, decision.Result);
        Assert.Equal(OrderStatus.CompensationPending, order.Status);
        var release = Assert.IsType<RequestInventoryRelease>(Assert.Single(decision.Commands));
        Assert.Equal(order.ReservationId, release.ReservationId);
        var retry = Assert.Single(order.Transitions.Skip(before));
        Assert.Equal("CompensationRetry", retry.Trigger);
        Assert.Equal(OrderStatus.CompensationFailed, retry.FromStatus);
        Assert.Equal(OrderStatus.CompensationPending, retry.ToStatus);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.InventoryReserved)]
    [InlineData(OrderStatus.PaymentAuthorized)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Failed)]
    public void Retry_is_rejected_from_unrelated_states(OrderStatus status)
    {
        var order = PendingOrder();
        order.Status = status;
        order.ReservationId = Guid.NewGuid();
        var transitions = order.Transitions.Count;

        var decision = OrderWorkflow.OnCompensationRetry(order, DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.Invalid, decision.Result);
        Assert.Empty(decision.Commands);
        Assert.Equal(status, order.Status);
        Assert.Equal(transitions, order.Transitions.Count);
    }

    [Fact]
    public void A_second_retry_while_compensation_is_pending_does_not_emit_another_release()
    {
        var order = CompensationFailedOrder();
        var first = OrderWorkflow.OnCompensationRetry(order, DateTimeOffset.UtcNow);
        var transitions = order.Transitions.Count;

        var duplicate = OrderWorkflow.OnCompensationRetry(order, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(WorkflowApplicationResult.Applied, first.Result);
        Assert.Equal(WorkflowApplicationResult.Duplicate, duplicate.Result);
        Assert.Empty(duplicate.Commands);
        Assert.Equal(OrderStatus.CompensationPending, order.Status);
        Assert.Equal(transitions, order.Transitions.Count);
    }

    [Fact]
    public void Duplicate_release_after_compensation_does_not_emit_another_command()
    {
        var order = CompensationFailedOrder();
        var reservationId = order.ReservationId!.Value;
        var first = OrderWorkflow.OnInventoryReleased(order, Guid.NewGuid(), reservationId, DateTimeOffset.UtcNow);
        var transitions = order.Transitions.Count;

        var duplicate = OrderWorkflow.OnInventoryReleased(order, Guid.NewGuid(), reservationId, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(WorkflowApplicationResult.Applied, first.Result);
        Assert.Equal(WorkflowApplicationResult.Duplicate, duplicate.Result);
        Assert.Empty(duplicate.Commands);
        Assert.Equal(OrderStatus.Failed, order.Status);
        Assert.Equal(transitions, order.Transitions.Count);
    }

    [Fact]
    public void Inventory_released_before_compensation_starts_is_ignored()
    {
        var pending = PendingOrder();
        var early = OrderWorkflow.OnInventoryReleased(pending, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.IgnoredLate, early.Result);
        Assert.Equal(OrderStatus.Pending, pending.Status);
        Assert.Empty(early.Commands);

        var confirmed = PendingOrder();
        OrderWorkflow.OnInventoryReserved(confirmed, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        OrderWorkflow.OnPaymentAuthorized(confirmed, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var late = OrderWorkflow.OnInventoryReleased(confirmed, Guid.NewGuid(), confirmed.ReservationId!.Value, DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowApplicationResult.IgnoredLate, late.Result);
        Assert.Equal(OrderStatus.Confirmed, confirmed.Status);
        Assert.Empty(late.Commands);
    }

    private static Order PendingOrder() => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = "customer-1",
        IdempotencyKey = "key",
        PaymentSimulation = "authorize",
        Currency = "USD",
        TotalAmount = 25m,
        Status = OrderStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Order ReservedOrder()
    {
        var order = PendingOrder();
        OrderWorkflow.OnInventoryReserved(order, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        return order;
    }

    private static Order CompensationFailedOrder()
    {
        var order = ReservedOrder();
        OrderWorkflow.OnPaymentFailed(order, Guid.NewGuid(), "declined", DateTimeOffset.UtcNow);
        OrderWorkflow.OnInventoryReleaseFailed(order, Guid.NewGuid(), "simulated", DateTimeOffset.UtcNow);
        return order;
    }
}
