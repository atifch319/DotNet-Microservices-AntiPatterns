namespace Commerce.Inventory.Domain;

public static class DemoCatalog
{
    public const string Widget = "WIDGET-1";
    public const string LimitedWidget = "WIDGET-2";
    public const string SoldOut = "SKU-SOLDOUT";
    public const string ReleaseFails = "SKU-RELEASE-FAIL";
}

public sealed class ReleaseFailureOptions
{
    public const string SectionName = "ReleaseFailure";

    public bool Enabled { get; set; } = true;
}

public static class ReservationStatus
{
    public const string Active = "Active";
    public const string Released = "Released";
    public const string Rejected = "Rejected";
}

public sealed class StockItem
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public int AvailableQuantity { get; set; }
    public int ReservedQuantity { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class InventoryReservation
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = ReservationStatus.Active;
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public List<ReservationLine> Lines { get; set; } = [];
}

public sealed class ReservationLine
{
    public Guid Id { get; set; }
    public Guid ReservationId { get; set; }
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
}

public readonly record struct ReservationLineRequest(string ProductId, int Quantity);

public abstract record ReservationOutcome
{
    public sealed record Reserved(Guid ReservationId) : ReservationOutcome;
    public sealed record Rejected(string Reason) : ReservationOutcome;
}

public abstract record ReleaseOutcome
{
    public sealed record Released(Guid ReservationId) : ReleaseOutcome;
    public sealed record Failed(string Reason) : ReleaseOutcome;
    public sealed record Missing() : ReleaseOutcome;
}

public static class StockRules
{
    public static bool CanReserve(int available, int requested) => requested > 0 && available >= requested;
}
