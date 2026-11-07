using Commerce.Contracts.Persistence;
using Commerce.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Commerce.Orders.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderTransition> OrderTransitions => Set<OrderTransition>();
    public DbSet<WorkflowIncident> WorkflowIncidents => Set<WorkflowIncident>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        MessagingModel.Configure(modelBuilder);

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.Property(x => x.CustomerId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.PaymentSimulation).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.FailureReason).HasMaxLength(500);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId);
            entity.HasMany(x => x.Transitions).WithOne().HasForeignKey(x => x.OrderId);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProductId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UnitPrice).HasPrecision(18, 2);
        });

        modelBuilder.Entity<OrderTransition>(entity =>
        {
            entity.ToTable("OrderTransitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Trigger).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<WorkflowIncident>(entity =>
        {
            entity.ToTable("WorkflowIncidents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Detail).HasMaxLength(500);
        });
    }
}

public sealed class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlServer("Server=localhost;Database=OrdersDb;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new OrdersDbContext(options);
    }
}
