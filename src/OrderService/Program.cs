using Commerce.Contracts.Hosting;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Orders.Api;
using Commerce.Orders.Application;
using Commerce.Orders.Data;
using Commerce.Orders.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseSqlServer(ConnectionStrings.Require(builder.Configuration, "Orders")));
builder.AddCommercePlatform("order-service");
builder.Services.AddReadyDatabase<OrdersDbContext>("orders-db");
builder.Services.AddScoped<OrderApplication>();
builder.Services.AddControllers();
builder.Services.AddHostedService<OutboxPublisherHostedService<OrdersDbContext>>();
builder.Services.AddRabbitConsumer<OrderWorkflowHandler>(Topology.OrderQueue);

var inventoryBaseUrl = builder.Configuration["Inventory:BaseUrl"];
if (!string.IsNullOrWhiteSpace(inventoryBaseUrl))
{
    var timeoutSeconds = builder.Configuration.GetValue("Inventory:TimeoutSeconds", 2);
    builder.Services.AddHttpClient<InventoryAvailabilityClient>(client =>
    {
        client.BaseAddress = new Uri(inventoryBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    });
}
else
{
    builder.Services.AddHttpClient<InventoryAvailabilityClient>();
}

var app = builder.Build();
app.UseCommercePipeline();
app.MapControllers();
await app.MigrateDatabaseAsync<OrdersDbContext>();
app.Run();

public partial class Program;
