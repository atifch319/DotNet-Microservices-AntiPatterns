using Commerce.Contracts.Persistence;
using Commerce.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Commerce.Inventory.Data;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<InventoryReservation> Reservations => Set<InventoryReservation>();
    public DbSet<ReservationLine> ReservationLines => Set<ReservationLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        MessagingModel.Configure(modelBuilder);

        modelBuilder.Entity<StockItem>(entity =>
        {
            entity.ToTable("StockItems");
            entity.HasKey(x => x.ProductId);
            entity.Property(x => x.ProductId).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<InventoryReservation>(entity =>
        {
            entity.ToTable("Reservations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrderId).IsUnique();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.FailureReason).HasMaxLength(500);
            entity.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ReservationId);
        });

        modelBuilder.Entity<ReservationLine>(entity =>
        {
            entity.ToTable("ReservationLines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProductId).HasMaxLength(64).IsRequired();
        });
    }
}

public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlServer("Server=localhost;Database=InventoryDb;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new InventoryDbContext(options);
    }
}

public static class InventorySeed
{
    public static async Task SeedAsync(InventoryDbContext db, CancellationToken cancellationToken)
    {
        if (await db.StockItems.AnyAsync(cancellationToken))
        {
            return;
        }

        db.StockItems.AddRange(
            new StockItem { ProductId = DemoCatalog.Widget, Name = "Standard widget", AvailableQuantity = 100 },
            new StockItem { ProductId = DemoCatalog.LimitedWidget, Name = "Limited widget", AvailableQuantity = 25 },
            new StockItem { ProductId = DemoCatalog.SoldOut, Name = "Sold-out sample", AvailableQuantity = 0 },
            new StockItem { ProductId = DemoCatalog.ReleaseFails, Name = "Simulated compensation failure", AvailableQuantity = 10 });
        await db.SaveChangesAsync(cancellationToken);
    }
}
