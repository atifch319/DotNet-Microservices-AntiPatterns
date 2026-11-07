using Commerce.Contracts.Hosting;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Payments.Application;
using Commerce.Payments.Data;
using Commerce.Payments.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseSqlServer(ConnectionStrings.Require(builder.Configuration, "Payments")));
builder.AddCommercePlatform("payment-service");
builder.Services.AddReadyDatabase<PaymentsDbContext>("payments-db");
builder.Services.AddScoped<PaymentApplication>();
builder.Services.AddControllers();
builder.Services.AddHostedService<OutboxPublisherHostedService<PaymentsDbContext>>();
builder.Services.AddRabbitConsumer<PaymentMessageHandler>(Topology.PaymentQueue);

var app = builder.Build();
app.UseCommercePipeline();
app.MapControllers();
await app.MigrateDatabaseAsync<PaymentsDbContext>();
app.Run();

public partial class Program;
