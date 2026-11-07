using Microsoft.EntityFrameworkCore;

namespace AntiPatterns.SharedDatabase;

/// <summary>
/// Problematic implementation. Do not register this context in the runnable services.
/// One model lets every service query and update every business table, so a schema change
/// in inventory deployment breaks payment and order at the same time.
/// </summary>
public sealed class SharedCommerceDbContext(DbContextOptions<SharedCommerceDbContext> options) : DbContext(options)
{
    public DbSet<SharedOrderRow> Orders => Set<SharedOrderRow>();
    public DbSet<SharedStockRow> Inventory => Set<SharedStockRow>();
    public DbSet<SharedPaymentRow> Payments => Set<SharedPaymentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SharedOrderRow>().ToTable("Orders").HasKey(row => row.Id);
        modelBuilder.Entity<SharedStockRow>().ToTable("Inventory").HasKey(row => row.ProductId);
        modelBuilder.Entity<SharedPaymentRow>().ToTable("Payments").HasKey(row => row.Id);
        modelBuilder.Entity<SharedPaymentRow>()
            .HasOne<SharedOrderRow>()
            .WithMany()
            .HasForeignKey(row => row.OrderId);
    }
}

public sealed class SharedOrderRow
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "Pending";
    public int Quantity { get; set; }
}

public sealed class SharedStockRow
{
    public string ProductId { get; set; } = "";
    public int AvailableQuantity { get; set; }
}

public sealed class SharedPaymentRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = "";
}

public static class CrossServiceWrites
{
    public static async Task OrderReadsInventoryAsync(SharedCommerceDbContext db, string productId, CancellationToken cancellationToken)
    {
        _ = await db.Inventory.SingleAsync(stock => stock.ProductId == productId, cancellationToken);
    }

    public static async Task PaymentUpdatesOrderAsync(SharedCommerceDbContext db, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleAsync(row => row.Id == orderId, cancellationToken);
        order.Status = "Paid";
    }
}
