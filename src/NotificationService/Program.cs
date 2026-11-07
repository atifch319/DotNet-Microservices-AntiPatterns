using Commerce.Contracts.Hosting;
using Commerce.Contracts.Messaging;
using Commerce.Notifications.Data;
using Commerce.Notifications.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddDbContext<NotificationsDbContext>(options =>
    options.UseSqlServer(ConnectionStrings.Require(builder.Configuration, "Notifications")));
builder.AddCommercePlatform("notification-service");
builder.Services.AddReadyDatabase<NotificationsDbContext>("notifications-db");
builder.Services.AddControllers();
builder.Services.AddRabbitConsumer<NotificationMessageHandler>(Topology.NotificationQueue);

var app = builder.Build();
app.UseCommercePipeline();
app.MapControllers();
await app.MigrateDatabaseAsync<NotificationsDbContext>();
app.Run();

public partial class Program;
