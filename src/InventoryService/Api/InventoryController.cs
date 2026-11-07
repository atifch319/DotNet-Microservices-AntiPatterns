using Commerce.Contracts.Hosting;
using Commerce.Inventory.Application;
using Commerce.Inventory.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Commerce.Inventory.Api;

[ApiController]
public sealed class InventoryController(InventoryApplication inventory, Inventory.Data.InventoryDbContext db) : ControllerBase
{
    [HttpGet("/api/inventory/{productId}")]
    public async Task<ActionResult<StockResponse>> Get(string productId, CancellationToken cancellationToken)
    {
        var stock = await inventory.GetAsync(productId, cancellationToken);
        return stock is null ? NotFound() : Ok(new StockResponse(stock.ProductId, stock.Name, stock.AvailableQuantity, stock.ReservedQuantity));
    }

    [HttpPost("/api/inventory/reservations")]
    public async Task<ActionResult<ReservationResponse>> Reserve([FromBody] CreateReservationRequest? request, CancellationToken cancellationToken)
    {
        request ??= new CreateReservationRequest();
        request.Lines ??= [];
        if (request.OrderId == Guid.Empty)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["orderId"] = ["OrderId is required."] });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var outcome = await inventory.ReserveAsync(
            request.OrderId,
            request.Lines.Select(line => new ReservationLineRequest(line.ProductId, line.Quantity)).ToList(),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return outcome switch
        {
            ReservationOutcome.Reserved reserved => Ok(new ReservationResponse(reserved.ReservationId, request.OrderId, ReservationStatus.Active, null)),
            ReservationOutcome.Rejected rejected => Conflict(new ReservationResponse(null, request.OrderId, ReservationStatus.Rejected, rejected.Reason)),
            _ => throw new InvalidOperationException("Unexpected reservation outcome.")
        };
    }

    [HttpPost("/api/inventory/reservations/{id:guid}/release")]
    public async Task<ActionResult<ReservationResponse>> Release(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var outcome = await inventory.ReleaseAsync(id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return outcome switch
        {
            ReleaseOutcome.Released => Ok(new ReservationResponse(id, null, ReservationStatus.Released, null)),
            ReleaseOutcome.Failed failed => Conflict(new { title = "Release failed", detail = failed.Reason }),
            ReleaseOutcome.Missing => NotFound(),
            _ => throw new InvalidOperationException("Unexpected release outcome.")
        };
    }
}

public sealed class CreateReservationRequest
{
    public Guid OrderId { get; set; }
    public List<ReservationLineBody> Lines { get; set; } = [];
}

public sealed class ReservationLineBody
{
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed record StockResponse(string ProductId, string Name, int AvailableQuantity, int ReservedQuantity);

public sealed record ReservationResponse(Guid? ReservationId, Guid? OrderId, string Status, string? Reason);
