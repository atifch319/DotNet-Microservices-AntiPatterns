using Commerce.Contracts.Events;
using Commerce.Contracts.Hosting;
using Commerce.Contracts.Messaging;
using Commerce.Inventory.Data;
using Commerce.Notifications.Data;
using Commerce.Orders.Data;
using Commerce.Payments.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationTests;

public class ArchitectureTests
{
    [Fact]
    public void Services_do_not_reference_each_other()
    {
        var root = Repo.Root();
        string[] services = ["OrderService", "InventoryService", "PaymentService", "NotificationService"];
        foreach (var service in services)
        {
            var project = File.ReadAllText(Path.Combine(root, "src", service, service + ".csproj"));
            foreach (var other in services.Where(name => name != service))
            {
                Assert.DoesNotContain(other + ".csproj", project, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("SharedDatabase.csproj", project, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Service_source_does_not_name_another_services_database()
    {
        var root = Repo.Root();
        var ownership = new Dictionary<string, string[]>
        {
            ["OrderService"] = ["InventoryDb", "PaymentsDb", "NotificationsDb"],
            ["InventoryService"] = ["OrdersDb", "PaymentsDb", "NotificationsDb"],
            ["PaymentService"] = ["OrdersDb", "InventoryDb", "NotificationsDb"],
            ["NotificationService"] = ["OrdersDb", "InventoryDb", "PaymentsDb"]
        };

        foreach (var (service, forbidden) in ownership)
        {
            var files = Directory.GetFiles(Path.Combine(root, "src", service), "*.cs", SearchOption.AllDirectories);
            var text = string.Join('\n', files.Select(File.ReadAllText));
            foreach (var name in forbidden)
            {
                Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("FromSqlRaw", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecuteSqlRaw", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Each_dbcontext_owns_only_its_business_tables_plus_messaging()
    {
        AssertModel<OrdersDbContext>("Order", "StockItem", "PaymentRecord", "NotificationRecord");
        AssertModel<InventoryDbContext>("StockItem", "Order", "PaymentRecord", "NotificationRecord");
        AssertModel<PaymentsDbContext>("PaymentRecord", "Order", "StockItem", "NotificationRecord");
        AssertModel<NotificationsDbContext>("NotificationRecord", "Order", "StockItem", "PaymentRecord");
    }

    [Fact]
    public void Every_integration_event_is_bound_to_exactly_one_queue()
    {
        var routingKeys = Topology.Queues.SelectMany(queue => queue.RoutingKeys).ToList();
        Assert.Equal(routingKeys.Count, routingKeys.Distinct().Count());
        Assert.Contains(EventTypes.ReserveInventory, routingKeys);
        Assert.Contains(EventTypes.OrderConfirmed, routingKeys);
        Assert.DoesNotContain(Topology.Queues, queue => queue.QueueName == Topology.InventoryQueue && queue.RoutingKeys.Contains(EventTypes.OrderPlaced));
    }

    [Theory]
    [InlineData(0, 5, true, DeliveryDisposition.Ack)]
    [InlineData(0, 5, false, DeliveryDisposition.Republish)]
    [InlineData(4, 5, false, DeliveryDisposition.DeadLetter)]
    [InlineData(2, 3, false, DeliveryDisposition.DeadLetter)]
    public void Delivery_policy_acks_retries_or_dead_letters(int attempt, int max, bool succeeded, DeliveryDisposition expected)
    {
        Assert.Equal(expected, DeliveryPolicy.Decide(attempt, max, succeeded));
    }

    [Fact]
    public async Task Correlation_id_is_copied_to_the_response_and_generated_when_missing()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "order-corr-1";
        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask, NullLogger<CorrelationMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal("order-corr-1", context.Response.Headers[CorrelationMiddleware.HeaderName]);

        var generated = new DefaultHttpContext();
        await middleware.InvokeAsync(generated);
        Assert.False(string.IsNullOrWhiteSpace(generated.Response.Headers[CorrelationMiddleware.HeaderName]));
    }

    private static void AssertModel<TContext>(string expected, params string[] absent)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseSqlServer("Server=localhost;Database=ModelInspection;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        var names = db.Model.GetEntityTypes().Select(entity => entity.ClrType.Name).ToHashSet();
        Assert.Contains(expected, names);
        Assert.Contains("OutboxMessage", names);
        Assert.Contains("InboxMessage", names);
        foreach (var name in absent)
        {
            Assert.DoesNotContain(name, names);
        }
    }
}

public static class Repo
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotNet-Microservices-AntiPatterns.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the solution file.");
    }
}
