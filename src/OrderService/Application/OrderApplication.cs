using Commerce.Contracts.Events;
using Commerce.Contracts.Hosting;
using Commerce.Contracts.Persistence;
using Commerce.Orders.Data;
using Commerce.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Orders.Application;

public sealed record CreateOrderLine(string ProductId, int Quantity, decimal UnitPrice);

public sealed record CreateOrder(string CustomerId, string PaymentSimulation, string Currency, IReadOnlyList<CreateOrderLine> Lines);

public enum CreateOrderStatus
{
    Created,
    Replay
}

public sealed record CreateOrderResult(CreateOrderStatus Status, Order Order);

public enum CompensationRetryStatus
{
    Accepted,
    NotFound,
    Rejected
}

public sealed record CompensationRetryResult(CompensationRetryStatus Status, Order? Order, string? Detail);

public sealed class OrderApplication(OrdersDbContext db)
{
    public async Task<CreateOrderResult> CreateAsync(CreateOrder request, string idempotencyKey, CancellationToken cancellationToken)
    {
        Validate(request, idempotencyKey);

        var existing = await db.Orders.Include(order => order.Items)
            .SingleOrDefaultAsync(order => order.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return new CreateOrderResult(CreateOrderStatus.Replay, existing);
        }

        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            CustomerId = request.CustomerId.Trim(),
            PaymentSimulation = request.PaymentSimulation.Trim().ToLowerInvariant(),
            Currency = request.Currency.Trim().ToUpperInvariant(),
            Status = OrderStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Items = request.Lines.Select(line => new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId.Trim(),
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice
            }).ToList()
        };
        order.TotalAmount = order.Items.Sum(item => item.UnitPrice * item.Quantity);
        foreach (var item in order.Items)
        {
            item.OrderId = order.Id;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Orders.Add(order);
        OutboxWriter.Add(db, EventTypes.OrderPlaced, 1, order.Id, null, new OrderPlacedV1(
            order.Id,
            order.CustomerId,
            order.TotalAmount,
            order.Currency,
            order.Items.Select(item => new OrderLineV1(item.ProductId, item.Quantity)).ToList()));
        OrderCommands.Write(db, order, [new RequestInventoryReservation(order.Id)], null);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreateOrderResult(CreateOrderStatus.Created, order);
    }

    public async Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await db.Orders
            .Include(order => order.Items)
            .Include(order => order.Transitions)
            .SingleOrDefaultAsync(order => order.Id == id, cancellationToken);
    }

    public async Task<CompensationRetryResult> RetryCompensationAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var order = await db.Orders
            .Include(row => row.Items)
            .Include(row => row.Transitions)
            .SingleOrDefaultAsync(row => row.Id == orderId, cancellationToken);
        if (order is null)
        {
            return new CompensationRetryResult(CompensationRetryStatus.NotFound, null, "Order was not found.");
        }

        var causationId = order.Transitions
            .Where(transition => transition.Trigger == "InventoryReleaseFailed")
            .OrderByDescending(transition => transition.OccurredAt)
            .Select(transition => transition.EventId)
            .FirstOrDefault();

        var decision = OrderWorkflow.OnCompensationRetry(order, DateTimeOffset.UtcNow);
        if (decision.Result != WorkflowApplicationResult.Applied)
        {
            return new CompensationRetryResult(CompensationRetryStatus.Rejected, order, decision.Detail);
        }

        var written = OrderCommands.Write(db, order, decision.Commands, causationId);
        var release = written.Single(message => message.EventType == EventTypes.ReleaseInventory);
        var retry = order.Transitions.Last(transition => transition.Trigger == "CompensationRetry");
        retry.EventId = release.Id;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return new CompensationRetryResult(CompensationRetryStatus.Rejected, null, "A compensation retry was already accepted.");
        }

        return new CompensationRetryResult(CompensationRetryStatus.Accepted, order, null);
    }

    private static void Validate(CreateOrder request, string idempotencyKey)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                list = [];
                errors[key] = list;
            }

            list.Add(message);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 80)
        {
            Add("Idempotency-Key", "A key of 1 to 80 characters is required.");
        }

        if (string.IsNullOrWhiteSpace(request.CustomerId) || request.CustomerId.Trim().Length > 64)
        {
            Add("customerId", "CustomerId is required and must be 64 characters or fewer.");
        }

        var simulation = request.PaymentSimulation?.Trim().ToLowerInvariant();
        if (simulation is not ("authorize" or "decline"))
        {
            Add("paymentSimulation", "Use authorize or decline. Do not send card numbers.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3)
        {
            Add("currency", "Currency must be a 3-letter code.");
        }

        if (request.Lines is null || request.Lines.Count is < 1 or > 20)
        {
            Add("lines", "Provide between 1 and 20 lines.");
        }
        else
        {
            for (var index = 0; index < request.Lines.Count; index++)
            {
                var line = request.Lines[index];
                if (string.IsNullOrWhiteSpace(line.ProductId) || line.ProductId.Trim().Length > 64)
                {
                    Add($"lines[{index}].productId", "ProductId is required.");
                }

                if (line.Quantity is < 1 or > 100)
                {
                    Add($"lines[{index}].quantity", "Quantity must be between 1 and 100.");
                }

                if (line.UnitPrice <= 0)
                {
                    Add($"lines[{index}].unitPrice", "UnitPrice must be greater than zero.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        }
    }
}

public static class OrderCommands
{
    public static List<OutboxMessage> Write(OrdersDbContext db, Order order, IReadOnlyList<WorkflowCommand> commands, Guid? causationId)
    {
        var written = new List<OutboxMessage>();
        foreach (var command in commands)
        {
            switch (command)
            {
                case RequestInventoryReservation:
                    written.Add(OutboxWriter.Add(db, EventTypes.ReserveInventory, 1, order.Id, causationId, new ReserveInventoryV1(
                        order.Id,
                        order.Items.Select(item => new OrderLineV1(item.ProductId, item.Quantity)).ToList())));
                    break;
                case RequestPaymentAuthorization payment:
                    written.Add(OutboxWriter.Add(db, EventTypes.AuthorizePayment, 1, order.Id, causationId, new AuthorizePaymentV1(order.Id, payment.Amount, payment.Simulation)));
                    break;
                case RequestInventoryRelease release:
                    written.Add(OutboxWriter.Add(db, EventTypes.ReleaseInventory, 1, order.Id, causationId, new ReleaseInventoryV1(order.Id, release.ReservationId)));
                    break;
                case NotifyOrderConfirmed confirmed:
                    written.Add(OutboxWriter.Add(db, EventTypes.OrderConfirmed, 1, order.Id, causationId, new OrderConfirmedV1(order.Id, confirmed.TotalAmount, confirmed.Currency)));
                    break;
                case NotifyOrderFailed failed:
                    written.Add(OutboxWriter.Add(db, EventTypes.OrderFailed, 1, order.Id, causationId, new OrderFailedV1(order.Id, failed.Reason)));
                    break;
                case NotifyCompensationFailed compensationFailed:
                    written.Add(OutboxWriter.Add(db, EventTypes.OrderCompensationFailed, 1, order.Id, causationId, new OrderCompensationFailedV1(order.Id, compensationFailed.Reason)));
                    break;
            }
        }

        return written;
    }
}
