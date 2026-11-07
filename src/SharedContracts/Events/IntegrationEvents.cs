namespace Commerce.Contracts.Events;

/// <summary>
/// Versioned integration event names. The version is part of the routing key so a consumer
/// can stay on v1 while a publisher introduces v2 on a new key.
/// </summary>
public static class EventTypes
{
    public const string OrderPlaced = "order.placed.v1";
    public const string ReserveInventory = "inventory.reserve.v1";
    public const string InventoryReserved = "inventory.reserved.v1";
    public const string InventoryReservationFailed = "inventory.reservation-failed.v1";
    public const string ReleaseInventory = "inventory.release.v1";
    public const string InventoryReleased = "inventory.released.v1";
    public const string InventoryReleaseFailed = "inventory.release-failed.v1";
    public const string AuthorizePayment = "payment.authorize.v1";
    public const string PaymentAuthorized = "payment.authorized.v1";
    public const string PaymentFailed = "payment.failed.v1";
    public const string OrderConfirmed = "order.confirmed.v1";
    public const string OrderFailed = "order.failed.v1";
    public const string OrderCompensationFailed = "order.compensation-failed.v1";
}

public sealed record OrderLineV1(string ProductId, int Quantity);

public sealed record OrderPlacedV1(Guid OrderId, string CustomerId, decimal TotalAmount, string Currency, IReadOnlyList<OrderLineV1> Lines);

public sealed record ReserveInventoryV1(Guid OrderId, IReadOnlyList<OrderLineV1> Lines);

public sealed record InventoryReservedV1(Guid OrderId, Guid ReservationId);

public sealed record InventoryReservationFailedV1(Guid OrderId, string Reason);

public sealed record ReleaseInventoryV1(Guid OrderId, Guid ReservationId);

public sealed record InventoryReleasedV1(Guid OrderId, Guid ReservationId);

public sealed record InventoryReleaseFailedV1(Guid OrderId, Guid ReservationId, string Reason);

/// <summary>
/// Simulation is only "authorize" or "decline". It is not a card number and must never be replaced with one.
/// </summary>
public sealed record AuthorizePaymentV1(Guid OrderId, decimal Amount, string Simulation);

public sealed record PaymentAuthorizedV1(Guid OrderId, Guid PaymentId, decimal Amount);

public sealed record PaymentFailedV1(Guid OrderId, string Reason);

public sealed record OrderConfirmedV1(Guid OrderId, decimal TotalAmount, string Currency);

public sealed record OrderFailedV1(Guid OrderId, string Reason);

public sealed record OrderCompensationFailedV1(Guid OrderId, string Reason);
