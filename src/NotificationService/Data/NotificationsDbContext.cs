using Commerce.Contracts.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Commerce.Notifications.Data;

public sealed class NotificationRecord
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Channel { get; set; } = "SimulatedEmail";
    public string Status { get; set; } = "Delivered";
    public string Template { get; set; } = "";
    public Guid SourceEventId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        MessagingModel.Configure(modelBuilder);
        modelBuilder.Entity<NotificationRecord>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.SourceEventId).IsUnique();
            entity.Property(x => x.Channel).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Template).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.OrderId);
        });
    }
}

public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer("Server=localhost;Database=NotificationsDb;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new NotificationsDbContext(options);
    }
}
