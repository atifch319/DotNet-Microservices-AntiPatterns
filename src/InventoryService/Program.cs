using Commerce.Contracts.Hosting;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Inventory.Application;
using Commerce.Inventory.Data;
using Commerce.Inventory.Domain;
using Commerce.Inventory.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(ConnectionStrings.Require(builder.Configuration, "Inventory")));
builder.AddCommercePlatform("inventory-service");
builder.Services.AddReadyDatabase<InventoryDbContext>("inventory-db");
builder.Services.Configure<ReleaseFailureOptions>(builder.Configuration.GetSection(ReleaseFailureOptions.SectionName));
builder.Services.AddScoped<InventoryApplication>();
builder.Services.AddControllers();
builder.Services.AddHostedService<OutboxPublisherHostedService<InventoryDbContext>>();
builder.Services.AddRabbitConsumer<InventoryMessageHandler>(Topology.InventoryQueue);

var app = builder.Build();
app.UseCommercePipeline();
app.MapControllers();
await app.MigrateDatabaseAsync<InventoryDbContext>();
await using (var scope = app.Services.CreateAsyncScope())
{
    await InventorySeed.SeedAsync(scope.ServiceProvider.GetRequiredService<InventoryDbContext>(), CancellationToken.None);
}

app.Run();

public partial class Program;
