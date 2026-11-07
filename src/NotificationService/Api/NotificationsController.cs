using Commerce.Contracts.Hosting;
using Commerce.Notifications.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Notifications.Api;

[ApiController]
public sealed class NotificationsController(NotificationsDbContext db) : ControllerBase
{
    [HttpGet("/api/notifications")]
    public async Task<ActionResult<IReadOnlyList<NotificationResponse>>> List([FromQuery] Guid? orderId, CancellationToken cancellationToken)
    {
        var query = db.Notifications.AsNoTracking().AsQueryable();
        if (orderId is Guid id)
        {
            query = query.Where(notification => notification.OrderId == id);
        }

        var rows = await query.OrderByDescending(notification => notification.CreatedAt).Take(100).ToListAsync(cancellationToken);
        return Ok(rows.Select(NotificationResponse.From).ToList());
    }

    [HttpGet("/api/notifications/{id:guid}")]
    public async Task<ActionResult<NotificationResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Notifications.AsNoTracking().SingleOrDefaultAsync(notification => notification.Id == id, cancellationToken);
        return row is null ? throw new NotFoundException($"Notification {id} was not found.") : Ok(NotificationResponse.From(row));
    }
}

public sealed record NotificationResponse(Guid Id, Guid OrderId, string Channel, string Status, string Template, DateTimeOffset CreatedAt)
{
    public static NotificationResponse From(NotificationRecord record) =>
        new(record.Id, record.OrderId, record.Channel, record.Status, record.Template, record.CreatedAt);
}
