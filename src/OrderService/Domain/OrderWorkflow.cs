namespace Commerce.Orders.Domain;

public enum OrderStatus
{
    Pending,
    InventoryReserved,
    PaymentAuthorized,
    Confirmed,
    Failed,
    CompensationPending,
    CompensationFailed
}

public enum WorkflowApplicationResult
{
    Applied,
    Duplicate,
    IgnoredLate,
    Invalid
}

public abstract record WorkflowCommand;

public sealed record RequestInventoryReservation(Guid OrderId) : WorkflowCommand;

public sealed record RequestPaymentAuthorization(Guid OrderId, decimal Amount, string Simulation) : WorkflowCommand;

public sealed record RequestInventoryRelease(Guid OrderId, Guid ReservationId) : WorkflowCommand;

public sealed record NotifyOrderConfirmed(Guid OrderId, decimal TotalAmount, string Currency) : WorkflowCommand;

public sealed record NotifyOrderFailed(Guid OrderId, string Reason) : WorkflowCommand;

public sealed record NotifyCompensationFailed(Guid OrderId, string Reason) : WorkflowCommand;

public sealed record WorkflowDecision(WorkflowApplicationResult Result, string? Detail, IReadOnlyList<WorkflowCommand> Commands)
{
    public static WorkflowDecision Applied(params WorkflowCommand[] commands) =>
        new(WorkflowApplicationResult.Applied, null, commands);

    public static WorkflowDecision Duplicate(string? detail = null) =>
        new(WorkflowApplicationResult.Duplicate, detail, []);

    public static WorkflowDecision Late(string detail) =>
        new(WorkflowApplicationResult.IgnoredLate, detail, []);

    public static WorkflowDecision Invalid(string detail) =>
        new(WorkflowApplicationResult.Invalid, detail, []);
}

public sealed class Order
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public string PaymentSimulation { get; set; } = "authorize";
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid? PaymentId { get; set; }
    public string? FailureReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<OrderItem> Items { get; set; } = [];
    public List<OrderTransition> Transitions { get; set; } = [];
}

public sealed class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class OrderTransition
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public OrderStatus FromStatus { get; set; }
    public OrderStatus ToStatus { get; set; }
    public string Trigger { get; set; } = "";
    public Guid? EventId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class WorkflowIncident
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public static class OrderWorkflow
{
    private static readonly HashSet<(OrderStatus From, OrderStatus To)> Legal =
    [
        (OrderStatus.Pending, OrderStatus.InventoryReserved),
        (OrderStatus.Pending, OrderStatus.Failed),
        (OrderStatus.InventoryReserved, OrderStatus.PaymentAuthorized),
        (OrderStatus.PaymentAuthorized, OrderStatus.Confirmed),
        (OrderStatus.InventoryReserved, OrderStatus.CompensationPending),
        (OrderStatus.CompensationPending, OrderStatus.Failed),
        (OrderStatus.CompensationPending, OrderStatus.CompensationFailed),
        (OrderStatus.CompensationFailed, OrderStatus.CompensationPending),
        (OrderStatus.CompensationFailed, OrderStatus.Failed)
    ];

    public static bool IsLegal(OrderStatus from, OrderStatus to) => Legal.Contains((from, to));

    public static WorkflowDecision OnInventoryReserved(Order order, Guid eventId, Guid reservationId, DateTimeOffset now)
    {
        if (order.ReservationId == reservationId
            && order.Status is OrderStatus.InventoryReserved or OrderStatus.PaymentAuthorized or OrderStatus.Confirmed or OrderStatus.CompensationPending or OrderStatus.Failed or OrderStatus.CompensationFailed)
        {
            return WorkflowDecision.Duplicate("Inventory reservation was already recorded.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            return WorkflowDecision.Late($"InventoryReserved cannot be applied from {order.Status}.");
        }

        Move(order, OrderStatus.InventoryReserved, "InventoryReserved", eventId, now);
        order.ReservationId = reservationId;
        return WorkflowDecision.Applied(new RequestPaymentAuthorization(order.Id, order.TotalAmount, order.PaymentSimulation));
    }

    public static WorkflowDecision OnInventoryReservationFailed(Order order, Guid eventId, string reason, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.Failed)
        {
            return WorkflowDecision.Duplicate("The order is already failed.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            return WorkflowDecision.Late($"InventoryReservationFailed cannot be applied from {order.Status}.");
        }

        order.FailureReason = reason;
        Move(order, OrderStatus.Failed, "InventoryReservationFailed", eventId, now);
        return WorkflowDecision.Applied(new NotifyOrderFailed(order.Id, reason));
    }

    public static WorkflowDecision OnPaymentAuthorized(Order order, Guid eventId, Guid paymentId, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.Confirmed && order.PaymentId == paymentId)
        {
            return WorkflowDecision.Duplicate("Payment authorization was already applied.");
        }

        if (order.Status != OrderStatus.InventoryReserved)
        {
            return WorkflowDecision.Late($"PaymentAuthorized cannot be applied from {order.Status}.");
        }

        order.PaymentId = paymentId;
        Move(order, OrderStatus.PaymentAuthorized, "PaymentAuthorized", eventId, now);
        Move(order, OrderStatus.Confirmed, "OrderConfirmed", eventId, now);
        return WorkflowDecision.Applied(new NotifyOrderConfirmed(order.Id, order.TotalAmount, order.Currency));
    }

    public static WorkflowDecision OnPaymentFailed(Order order, Guid eventId, string reason, DateTimeOffset now)
    {
        if (order.Status is OrderStatus.CompensationPending or OrderStatus.CompensationFailed or OrderStatus.Failed)
        {
            return WorkflowDecision.Duplicate("Payment failure was already accepted.");
        }

        if (order.Status != OrderStatus.InventoryReserved || order.ReservationId is null)
        {
            return WorkflowDecision.Late($"PaymentFailed cannot be applied from {order.Status}.");
        }

        order.FailureReason = reason;
        Move(order, OrderStatus.CompensationPending, "PaymentFailed", eventId, now);
        return WorkflowDecision.Applied(new RequestInventoryRelease(order.Id, order.ReservationId.Value));
    }

    public static WorkflowDecision OnInventoryReleased(Order order, Guid eventId, Guid reservationId, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.Failed)
        {
            return WorkflowDecision.Duplicate("Inventory release was already recorded.");
        }

        if (order.Status is not (OrderStatus.CompensationPending or OrderStatus.CompensationFailed))
        {
            return WorkflowDecision.Late($"InventoryReleased cannot be applied from {order.Status}.");
        }

        if (order.ReservationId != reservationId)
        {
            return WorkflowDecision.Late("The released reservation does not match this order.");
        }

        Move(order, OrderStatus.Failed, "InventoryReleased", eventId, now);
        return WorkflowDecision.Applied(new NotifyOrderFailed(order.Id, order.FailureReason ?? "Payment was not authorized."));
    }

    public static WorkflowDecision OnInventoryReleaseFailed(Order order, Guid eventId, string reason, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.CompensationFailed)
        {
            return WorkflowDecision.Duplicate("Compensation failure was already recorded.");
        }

        if (order.Status != OrderStatus.CompensationPending)
        {
            return WorkflowDecision.Late($"InventoryReleaseFailed cannot be applied from {order.Status}.");
        }

        order.FailureReason = reason;
        Move(order, OrderStatus.CompensationFailed, "InventoryReleaseFailed", eventId, now);
        return WorkflowDecision.Applied(new NotifyCompensationFailed(order.Id, reason));
    }

    public static WorkflowDecision OnCompensationRetry(Order order, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.CompensationPending && order.ReservationId is not null)
        {
            return WorkflowDecision.Duplicate("A compensation retry is already in progress.");
        }

        if (order.Status != OrderStatus.CompensationFailed || order.ReservationId is null)
        {
            return WorkflowDecision.Invalid($"Compensation retry requires CompensationFailed and a reservation. The order is {order.Status}.");
        }

        Move(order, OrderStatus.CompensationPending, "CompensationRetry", Guid.Empty, now);
        return WorkflowDecision.Applied(new RequestInventoryRelease(order.Id, order.ReservationId.Value));
    }

    private static void Move(Order order, OrderStatus to, string trigger, Guid eventId, DateTimeOffset now)
    {
        var from = order.Status;
        if (!IsLegal(from, to))
        {
            throw new InvalidOperationException($"Illegal order transition {from} -> {to}.");
        }

        order.Transitions.Add(new OrderTransition
        {
            OrderId = order.Id,
            FromStatus = from,
            ToStatus = to,
            Trigger = trigger,
            EventId = eventId,
            OccurredAt = now
        });
        order.Status = to;
        order.UpdatedAt = now;
    }
}
