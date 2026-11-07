using Commerce.Contracts.Hosting;
using Commerce.Inventory.Data;
using Commerce.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Commerce.Inventory.Application;

public sealed class InventoryApplication
{
    private readonly InventoryDbContext db;
    private readonly ReleaseFailureOptions releaseFailure;

    public InventoryApplication(InventoryDbContext db)
        : this(db, new ReleaseFailureOptions())
    {
    }

    public InventoryApplication(InventoryDbContext db, ReleaseFailureOptions releaseFailure)
    {
        this.db = db;
        this.releaseFailure = releaseFailure;
    }

    public InventoryApplication(InventoryDbContext db, IOptions<ReleaseFailureOptions> releaseFailure)
        : this(db, releaseFailure.Value)
    {
    }

    public async Task<StockItem?> GetAsync(string productId, CancellationToken cancellationToken)
    {
        return await db.StockItems.AsNoTracking().SingleOrDefaultAsync(item => item.ProductId == productId, cancellationToken);
    }

    public async Task<ReservationOutcome> ReserveAsync(Guid orderId, IReadOnlyList<ReservationLineRequest> lines, CancellationToken cancellationToken)
    {
        ValidateLines(lines);
        var existing = await db.Reservations.Include(reservation => reservation.Lines)
            .SingleOrDefaultAsync(reservation => reservation.OrderId == orderId, cancellationToken);
        if (existing is not null)
        {
            return existing.Status == ReservationStatus.Active
                ? new ReservationOutcome.Reserved(existing.Id)
                : new ReservationOutcome.Rejected(existing.FailureReason ?? "Reservation was already rejected.");
        }

        var productIds = lines.Select(line => line.ProductId).Distinct().ToList();
        var stock = await db.StockItems.Where(item => productIds.Contains(item.ProductId)).ToListAsync(cancellationToken);
        if (stock.Count != productIds.Count)
        {
            return Reject(orderId, lines, "One or more products do not exist.");
        }

        foreach (var line in lines)
        {
            var item = stock.Single(stockItem => stockItem.ProductId == line.ProductId);
            if (!StockRules.CanReserve(item.AvailableQuantity, line.Quantity))
            {
                return Reject(orderId, lines, $"Insufficient available quantity for {line.ProductId}.");
            }
        }

        foreach (var line in lines)
        {
            var item = stock.Single(stockItem => stockItem.ProductId == line.ProductId);
            item.AvailableQuantity -= line.Quantity;
            item.ReservedQuantity += line.Quantity;
        }

        var reservation = CreateReservation(orderId, lines, ReservationStatus.Active, null);
        reservation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        db.Reservations.Add(reservation);
        return new ReservationOutcome.Reserved(reservation.Id);
    }

    public async Task<ReleaseOutcome> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations.Include(row => row.Lines)
            .SingleOrDefaultAsync(row => row.Id == reservationId, cancellationToken);
        if (reservation is null)
        {
            return new ReleaseOutcome.Missing();
        }

        return await ReleaseReservationAsync(reservation, cancellationToken);
    }

    public async Task<ReleaseOutcome> ReleaseByOrderAsync(Guid orderId, Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await db.Reservations.Include(row => row.Lines)
            .SingleOrDefaultAsync(row => row.OrderId == orderId, cancellationToken);
        if (reservation is null || reservation.Id != reservationId)
        {
            return new ReleaseOutcome.Missing();
        }

        return await ReleaseReservationAsync(reservation, cancellationToken);
    }

    private async Task<ReleaseOutcome> ReleaseReservationAsync(InventoryReservation reservation, CancellationToken cancellationToken)
    {
        if (reservation.Status == ReservationStatus.Released)
        {
            return new ReleaseOutcome.Released(reservation.Id);
        }

        if (reservation.Status != ReservationStatus.Active)
        {
            return new ReleaseOutcome.Failed("Only an active reservation can be released.");
        }

        if (releaseFailure.Enabled && reservation.Lines.Any(line => string.Equals(line.ProductId, DemoCatalog.ReleaseFails, StringComparison.OrdinalIgnoreCase)))
        {
            return new ReleaseOutcome.Failed(
                "Simulated compensation failure for SKU-RELEASE-FAIL. The reservation is still active and needs manual intervention.");
        }

        var productIds = reservation.Lines.Select(line => line.ProductId).ToList();
        var stock = await db.StockItems.Where(item => productIds.Contains(item.ProductId)).ToListAsync(cancellationToken);
        foreach (var line in reservation.Lines)
        {
            var item = stock.Single(stockItem => stockItem.ProductId == line.ProductId);
            item.ReservedQuantity -= line.Quantity;
            item.AvailableQuantity += line.Quantity;
        }

        reservation.Status = ReservationStatus.Released;
        reservation.ReleasedAt = DateTimeOffset.UtcNow;
        return new ReleaseOutcome.Released(reservation.Id);
    }

    private ReservationOutcome Reject(Guid orderId, IReadOnlyList<ReservationLineRequest> lines, string reason)
    {
        db.Reservations.Add(CreateReservation(orderId, lines, ReservationStatus.Rejected, reason));
        return new ReservationOutcome.Rejected(reason);
    }

    private static InventoryReservation CreateReservation(Guid orderId, IReadOnlyList<ReservationLineRequest> lines, string status, string? reason)
    {
        var reservation = new InventoryReservation
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Status = status,
            FailureReason = reason,
            CreatedAt = DateTimeOffset.UtcNow
        };
        reservation.Lines = lines.Select(line => new ReservationLine
        {
            Id = Guid.NewGuid(),
            ReservationId = reservation.Id,
            ProductId = line.ProductId,
            Quantity = line.Quantity
        }).ToList();
        return reservation;
    }

    private static void ValidateLines(IReadOnlyList<ReservationLineRequest> lines)
    {
        var errors = new Dictionary<string, string[]>();
        if (lines.Count is < 1 or > 20)
        {
            errors["lines"] = ["Provide between 1 and 20 lines."];
        }

        if (lines.Select(line => line.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lines.Count)
        {
            errors["lines"] = ["Duplicate product ids are not allowed on one reservation."];
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index].ProductId))
            {
                errors[$"lines[{index}].productId"] = ["ProductId is required."];
            }

            if (lines[index].Quantity is < 1 or > 100)
            {
                errors[$"lines[{index}].quantity"] = ["Quantity must be between 1 and 100."];
            }
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }
}
