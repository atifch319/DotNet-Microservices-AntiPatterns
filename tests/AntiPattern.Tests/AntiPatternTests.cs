using System.Text.Json;
using AntiPatterns.DeploymentCoupling;
using AntiPatterns.SharedDatabase;
using AntiPatterns.SynchronousChain;
using AntiPatterns.WrongServiceBoundaries;
using Microsoft.EntityFrameworkCore;

namespace AntiPattern.Tests;

public class SynchronousChainTests
{
    [Fact]
    public async Task Latency_accumulates_across_the_call_chain()
    {
        var hop = TimeSpan.FromMilliseconds(40);
        var chain = new SynchronousPlaceOrderChain(new DelayingHopClient(hop), new DelayingHopClient(hop), new DelayingHopClient(hop));

        var trace = await chain.ExecuteAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.False(trace.TimedOut);
        Assert.Equal(["inventory", "payment", "notification"], trace.CompletedHops);
        Assert.True(trace.Elapsed >= TimeSpan.FromMilliseconds(110));
    }

    [Fact]
    public async Task A_timeout_cancels_the_remaining_hops()
    {
        var hop = TimeSpan.FromMilliseconds(80);
        var chain = new SynchronousPlaceOrderChain(new DelayingHopClient(hop), new DelayingHopClient(hop), new DelayingHopClient(hop));

        var trace = await chain.ExecuteAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.True(trace.TimedOut);
        Assert.DoesNotContain("notification", trace.CompletedHops);
        Assert.True(trace.CompletedHops.Count < 3);
    }
}

public class WrongBoundaryTests
{
    [Fact]
    public async Task Technical_services_require_multiple_remote_calls()
    {
        var log = new RemoteCallLog();
        var controller = new LayeredControllerService(new RemoteValidationService(log), new RemoteRepositoryService(log), log);

        var hops = await controller.PlaceAsync(new OrderDraft("customer", "WIDGET-1", 1), CancellationToken.None);

        Assert.Equal(3, hops);
        Assert.Equal(["controller", "validation-service", "repository-service"], log.Calls);
    }

    [Fact]
    public void A_business_capability_can_validate_and_save_in_one_module()
    {
        var module = new OrderPlacementModule();
        Assert.Equal(1, module.Place(new OrderDraft("customer", "WIDGET-1", 1)));
    }
}

public class DeploymentCouplingTests
{
    [Fact]
    public void An_optional_new_field_does_not_break_a_v1_consumer()
    {
        var orderId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            orderId,
            totalAmount = 42.5m,
            currency = "USD",
            loyaltyPoints = 5
        });

        Assert.True(ContractCompatibility.CanDeserializeOrderConfirmedV1(json));
        Assert.Empty(ContractCompatibility.FindRemovedRequiredFields(json));
    }

    [Fact]
    public void Removing_a_required_field_is_a_breaking_change()
    {
        var json = JsonSerializer.Serialize(new { id = Guid.NewGuid(), amount = 10 });
        var removed = ContractCompatibility.FindRemovedRequiredFields(json);

        Assert.Contains("orderId", removed);
        Assert.Contains("totalAmount", removed);
        Assert.Contains("currency", removed);
        Assert.False(ContractCompatibility.CanDeserializeOrderConfirmedV1(json));
    }

    [Fact]
    public void The_shared_database_model_maps_every_services_tables()
    {
        var options = new DbContextOptionsBuilder<SharedCommerceDbContext>()
            .UseSqlServer("Server=localhost;Database=SharedAntiPattern;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new SharedCommerceDbContext(options);
        var names = db.Model.GetEntityTypes().Select(entity => entity.ClrType.Name).ToHashSet();

        Assert.Contains("SharedOrderRow", names);
        Assert.Contains("SharedStockRow", names);
        Assert.Contains("SharedPaymentRow", names);
    }
}
