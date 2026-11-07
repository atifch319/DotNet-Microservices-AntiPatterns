using System.Text;
using Commerce.Contracts.Events;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Persistence;
using Commerce.Inventory.Application;
using Commerce.Inventory.Data;
using Commerce.Inventory.Domain;
using Commerce.Inventory.Messaging;
using Commerce.Notifications.Data;
using Commerce.Notifications.Messaging;
using Commerce.Orders.Application;
using Commerce.Orders.Data;
using Commerce.Orders.Domain;
using Commerce.Orders.Messaging;
using Commerce.Payments.Application;
using Commerce.Payments.Data;
using Commerce.Payments.Domain;
using Commerce.Payments.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationTests;

[Collection("sql")]
public class SqlWorkflowTests(SqlDatabases databases)
{
    [SqlFact]
    public async Task Order_and_outbox_commit_or_roll_back_together()
    {
        await using var db = databases.Orders();
        var order = NewPendingOrder();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Orders.Add(order);
            OutboxWriter.Add(db, EventTypes.OrderPlaced, 1, order.Id, null, new OrderPlacedV1(order.Id, order.CustomerId, 10m, "USD", []));
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await db.Orders.CountAsync(row => row.Id == order.Id));
        Assert.Equal(0, await db.Set<OutboxMessage>().CountAsync(row => row.CorrelationId == order.Id));

        db.ChangeTracker.Clear();
        await using var committed = await db.Database.BeginTransactionAsync();
        db.Orders.Add(order);
        OutboxWriter.Add(db, EventTypes.OrderPlaced, 1, order.Id, null, new OrderPlacedV1(order.Id, order.CustomerId, 10m, "USD", []));
        await db.SaveChangesAsync();
        await committed.CommitAsync();

        Assert.Equal(1, await db.Orders.CountAsync(row => row.Id == order.Id));
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync(row => row.CorrelationId == order.Id && row.PublishedAt == null));
    }

    [SqlFact]
    public async Task Failed_publication_is_retried_and_then_stops()
    {
        await using var db = databases.Orders();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = EventTypes.OrderPlaced,
            EventVersion = 1,
            CorrelationId = Guid.NewGuid(),
            Payload = "{}",
            OccurredAt = DateTimeOffset.UtcNow
        };
        db.Set<OutboxMessage>().Add(message);
        await db.SaveChangesAsync();

        var dispatcher = new OutboxDispatcher(NullLogger<OutboxDispatcher>.Instance);
        var options = new OutboxOptions { MaxAttempts = 3, BatchSize = 20, LockSeconds = 30 };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dispatcher.DispatchBatchAsync(db, new ThrowingBroker(), options, CancellationToken.None);
        }

        var stored = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(row => row.Id == message.Id);
        Assert.Null(stored.PublishedAt);
        Assert.Equal(3, stored.AttemptCount);
        Assert.False(string.IsNullOrWhiteSpace(stored.LastError));
    }

    [SqlFact]
    public async Task A_locked_outbox_row_is_not_published_by_a_second_dispatcher()
    {
        await using var db = databases.Orders();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = EventTypes.OrderPlaced,
            EventVersion = 1,
            CorrelationId = Guid.NewGuid(),
            Payload = "{}",
            OccurredAt = DateTimeOffset.UtcNow,
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(5),
            LockToken = Guid.NewGuid()
        };
        db.Set<OutboxMessage>().Add(message);
        await db.SaveChangesAsync();

        var broker = new RecordingBroker();
        var dispatcher = new OutboxDispatcher(NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.DispatchBatchAsync(db, broker, new OutboxOptions { MaxAttempts = 5, BatchSize = 20, LockSeconds = 30 }, CancellationToken.None);

        Assert.DoesNotContain(broker.Messages, row => row.Id == message.Id);
        var stored = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(row => row.Id == message.Id);
        Assert.Null(stored.PublishedAt);
    }

    [SqlFact]
    public async Task Successful_publication_is_marked_only_after_the_broker_accepts_it()
    {
        await using var db = databases.Orders();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = EventTypes.OrderPlaced,
            EventVersion = 1,
            CorrelationId = Guid.NewGuid(),
            Payload = """{"eventId":"00000000-0000-0000-0000-000000000001","eventType":"order.placed.v1","eventVersion":1,"correlationId":"00000000-0000-0000-0000-000000000001","occurredAt":"2026-11-06T00:00:00Z","payload":"{}"}""",
            OccurredAt = DateTimeOffset.UtcNow
        };
        db.Set<OutboxMessage>().Add(message);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var broker = new RecordingBroker();
        await new OutboxDispatcher(NullLogger<OutboxDispatcher>.Instance)
            .DispatchBatchAsync(db, broker, new OutboxOptions { MaxAttempts = 5, BatchSize = 20, LockSeconds = 30 }, CancellationToken.None);

        Assert.Contains(broker.Messages, row => row.Id == message.Id);
        var stored = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(row => row.Id == message.Id);
        Assert.NotNull(stored.PublishedAt);
    }

    [SqlFact]
    public async Task A_failed_consumer_transaction_does_not_mark_the_event_processed()
    {
        await using var db = databases.Orders();
        var envelope = Envelope(EventTypes.OrderPlaced, Guid.NewGuid(), "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InboxTransaction.RunAsync(db, "test-consumer", envelope, _ => throw new InvalidOperationException("transient"), CancellationToken.None));

        Assert.Equal(0, await db.Set<InboxMessage>().CountAsync(row => row.EventId == envelope.EventId));

        var calls = 0;
        await InboxTransaction.RunAsync(db, "test-consumer", envelope, _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await InboxTransaction.RunAsync(db, "test-consumer", envelope, _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(1, await db.Set<InboxMessage>().CountAsync(row => row.EventId == envelope.EventId));
    }

    [SqlFact]
    public async Task Repeated_order_creation_with_the_same_key_returns_the_original_order()
    {
        await using var db = databases.Orders();
        var application = new OrderApplication(db);
        var key = "idem-" + Guid.NewGuid().ToString("N");
        var request = new CreateOrder("customer-1", "authorize", "USD", [new CreateOrderLine(DemoCatalog.Widget, 1, 12m)]);
        var first = await application.CreateAsync(request, key, CancellationToken.None);
        var second = await application.CreateAsync(request, key, CancellationToken.None);

        Assert.Equal(CreateOrderStatus.Created, first.Status);
        Assert.Equal(CreateOrderStatus.Replay, second.Status);
        Assert.Equal(first.Order.Id, second.Order.Id);
        Assert.Equal(1, await db.Orders.CountAsync(order => order.IdempotencyKey == key));
    }

    [SqlFact]
    public async Task Saga_confirms_an_order_and_records_each_local_outcome()
    {
        var order = await PlaceOrderAsync("authorize", DemoCatalog.Widget, 1);
        await PumpAsync(order.Id);

        await using var orders = databases.Orders();
        var stored = await orders.Orders.Include(row => row.Transitions).SingleAsync(row => row.Id == order.Id);
        Assert.Equal(OrderStatus.Confirmed, stored.Status);
        Assert.NotNull(stored.ReservationId);
        Assert.NotNull(stored.PaymentId);
        Assert.Contains(stored.Transitions, transition => transition.ToStatus == OrderStatus.PaymentAuthorized);

        await using var inventory = databases.Inventory();
        var stock = await inventory.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.Widget);
        var reservation = await inventory.Reservations.SingleAsync(row => row.OrderId == order.Id);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.True(stock.ReservedQuantity >= 1);

        await using var payments = databases.Payments();
        var payment = await payments.Payments.SingleAsync(row => row.OrderId == order.Id);
        Assert.Equal(PaymentStatus.Authorized, payment.Status);

        await using var notifications = databases.Notifications();
        var templates = await notifications.Notifications.Where(row => row.OrderId == order.Id).Select(row => row.Template).ToListAsync();
        Assert.Contains("OrderReceived", templates);
        Assert.Contains("OrderConfirmed", templates);
        Assert.Equal(0, await UnpublishedForAsync(order.Id));
    }

    [SqlFact]
    public async Task Inventory_rejection_fails_the_order_without_a_payment()
    {
        var order = await PlaceOrderAsync("authorize", DemoCatalog.SoldOut, 1);
        await PumpAsync(order.Id);

        await using var orders = databases.Orders();
        var stored = await orders.Orders.SingleAsync(row => row.Id == order.Id);
        Assert.Equal(OrderStatus.Failed, stored.Status);
        Assert.Null(stored.PaymentId);

        await using var payments = databases.Payments();
        Assert.Equal(0, await payments.Payments.CountAsync(row => row.OrderId == order.Id));
    }

    [SqlFact]
    public async Task Payment_rejection_releases_inventory_and_then_fails_the_order()
    {
        await using (var before = databases.Inventory())
        {
            var stock = await before.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.LimitedWidget);
            stock.AvailableQuantity = 25;
            stock.ReservedQuantity = 0;
            await before.SaveChangesAsync();
        }

        var order = await PlaceOrderAsync("decline", DemoCatalog.LimitedWidget, 2);
        await PumpAsync(order.Id);

        await using var orders = databases.Orders();
        var stored = await orders.Orders.SingleAsync(row => row.Id == order.Id);
        Assert.Equal(OrderStatus.Failed, stored.Status);
        Assert.Contains(await orders.OrderTransitions.Where(row => row.OrderId == order.Id).ToListAsync(), transition => transition.ToStatus == OrderStatus.CompensationPending);

        await using var inventory = databases.Inventory();
        var reservation = await inventory.Reservations.SingleAsync(row => row.OrderId == order.Id);
        var stockAfter = await inventory.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.LimitedWidget);
        Assert.Equal(ReservationStatus.Released, reservation.Status);
        Assert.Equal(25, stockAfter.AvailableQuantity);
        Assert.Equal(0, stockAfter.ReservedQuantity);

        await using var payments = databases.Payments();
        Assert.Equal(PaymentStatus.Declined, (await payments.Payments.SingleAsync(row => row.OrderId == order.Id)).Status);
    }

    [SqlFact]
    public async Task Compensation_failure_leaves_the_order_unfailed_and_stock_reserved()
    {
        await using (var stockDb = databases.Inventory())
        {
            var stock = await stockDb.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.ReleaseFails);
            stock.AvailableQuantity = Math.Max(stock.AvailableQuantity, 5);
            await stockDb.SaveChangesAsync();
        }

        var order = await PlaceOrderAsync("decline", DemoCatalog.ReleaseFails, 1);
        await PumpAsync(order.Id);

        await using var orders = databases.Orders();
        var stored = await orders.Orders.SingleAsync(row => row.Id == order.Id);
        Assert.Equal(OrderStatus.CompensationFailed, stored.Status);
        Assert.NotEqual(OrderStatus.Failed, stored.Status);

        await using var inventory = databases.Inventory();
        var reservation = await inventory.Reservations.SingleAsync(row => row.OrderId == order.Id);
        Assert.Equal(ReservationStatus.Active, reservation.Status);

        await using var notifications = databases.Notifications();
        Assert.Contains("CompensationFailed", await notifications.Notifications.Where(row => row.OrderId == order.Id).Select(row => row.Template).ToListAsync());
    }

    [SqlFact]
    public async Task Compensation_retry_is_accepted_once_and_records_a_new_release_command()
    {
        var causationId = Guid.NewGuid();
        var order = await SaveCompensationFailedAsync(causationId);
        await using var db = databases.Orders();
        var application = new OrderApplication(db);

        var first = await application.RetryCompensationAsync(order.Id, CancellationToken.None);
        var second = await application.RetryCompensationAsync(order.Id, CancellationToken.None);

        Assert.Equal(CompensationRetryStatus.Accepted, first.Status);
        Assert.Equal(OrderStatus.CompensationPending, first.Order!.Status);
        Assert.Equal(CompensationRetryStatus.Rejected, second.Status);

        var release = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(message =>
            message.CorrelationId == order.Id && message.EventType == EventTypes.ReleaseInventory);
        Assert.NotEqual(causationId, release.Id);
        Assert.Equal(causationId, release.CausationId);
        Assert.Equal(order.Id, release.CorrelationId);
        var stored = await db.Orders.Include(row => row.Transitions).SingleAsync(row => row.Id == order.Id);
        Assert.Equal(release.Id, stored.Transitions.Single(transition => transition.Trigger == "CompensationRetry").EventId);
    }

    [SqlFact]
    public async Task Concurrent_compensation_retries_persist_one_release_command()
    {
        var order = await SaveCompensationFailedAsync(Guid.NewGuid());
        await using var firstDb = databases.Orders();
        await using var secondDb = databases.Orders();
        var results = await Task.WhenAll(
            new OrderApplication(firstDb).RetryCompensationAsync(order.Id, CancellationToken.None),
            new OrderApplication(secondDb).RetryCompensationAsync(order.Id, CancellationToken.None));

        Assert.Equal(1, results.Count(result => result.Status == CompensationRetryStatus.Accepted));
        Assert.Equal(1, results.Count(result => result.Status == CompensationRetryStatus.Rejected));
        await using var check = databases.Orders();
        Assert.Equal(1, await check.Set<OutboxMessage>().CountAsync(message =>
            message.CorrelationId == order.Id && message.EventType == EventTypes.ReleaseInventory));
    }

    [SqlFact]
    public async Task Compensation_retry_outbox_rolls_back_with_the_order_change()
    {
        var order = await SaveCompensationFailedAsync(Guid.NewGuid());
        await using var db = databases.Orders();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var tracked = await db.Orders.Include(row => row.Transitions).SingleAsync(row => row.Id == order.Id);
        var decision = OrderWorkflow.OnCompensationRetry(tracked, DateTimeOffset.UtcNow);
        var causationId = tracked.Transitions.Single(transition => transition.Trigger == "InventoryReleaseFailed").EventId;
        OrderCommands.Write(db, tracked, decision.Commands, causationId);
        await db.SaveChangesAsync();
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();

        var stored = await db.Orders.AsNoTracking().SingleAsync(row => row.Id == order.Id);
        Assert.Equal(OrderStatus.CompensationFailed, stored.Status);
        Assert.Equal(0, await db.Set<OutboxMessage>().CountAsync(message =>
            message.CorrelationId == order.Id && message.EventType == EventTypes.ReleaseInventory));
    }

    [SqlFact]
    public async Task Disabling_release_failure_restores_the_reserved_quantity()
    {
        var orderId = Guid.NewGuid();
        await using var db = databases.Inventory();
        var stock = await db.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.ReleaseFails);
        if (stock.AvailableQuantity < 1)
        {
            stock.AvailableQuantity += 1;
            await db.SaveChangesAsync();
        }

        var available = stock.AvailableQuantity;
        var reserved = stock.ReservedQuantity;
        var outcome = await new InventoryApplication(db, new ReleaseFailureOptions { Enabled = true })
            .ReserveAsync(orderId, [new ReservationLineRequest(DemoCatalog.ReleaseFails, 1)], CancellationToken.None);
        await db.SaveChangesAsync();
        var reservationId = Assert.IsType<ReservationOutcome.Reserved>(outcome).ReservationId;

        var blocked = await new InventoryApplication(db, new ReleaseFailureOptions { Enabled = true })
            .ReleaseByOrderAsync(orderId, reservationId, CancellationToken.None);
        Assert.IsType<ReleaseOutcome.Failed>(blocked);

        var released = await new InventoryApplication(db, new ReleaseFailureOptions { Enabled = false })
            .ReleaseByOrderAsync(orderId, reservationId, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.IsType<ReleaseOutcome.Released>(released);
        var after = await db.StockItems.AsNoTracking().SingleAsync(item => item.ProductId == DemoCatalog.ReleaseFails);
        Assert.Equal(available, after.AvailableQuantity);
        Assert.Equal(reserved, after.ReservedQuantity);
        Assert.Equal(ReservationStatus.Released, (await db.Reservations.AsNoTracking().SingleAsync(row => row.Id == reservationId)).Status);
    }

    [SqlFact]
    public async Task Two_dispatchers_cannot_publish_the_same_outbox_row()
    {
        await using var seed = databases.Orders();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventType = EventTypes.OrderPlaced,
            EventVersion = 1,
            CorrelationId = Guid.NewGuid(),
            Payload = "{}",
            OccurredAt = DateTimeOffset.UtcNow
        };
        seed.Set<OutboxMessage>().Add(message);
        await seed.SaveChangesAsync();
        var parkedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        await seed.Set<OutboxMessage>()
            .Where(row => row.Id != message.Id && row.PublishedAt == null && row.LockedUntil == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LockedUntil, parkedUntil));

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBroker = new HoldBroker(message.Id, hold.Task);
        var secondBroker = new RecordingBroker();
        await using var firstDb = databases.Orders();
        await using var secondDb = databases.Orders();
        var dispatcher = new OutboxDispatcher(NullLogger<OutboxDispatcher>.Instance);
        var options = new OutboxOptions { MaxAttempts = 5, BatchSize = 20, LockSeconds = 30 };
        try
        {
            var first = dispatcher.DispatchBatchAsync(firstDb, firstBroker, options, CancellationToken.None);
            var locked = false;
            for (var attempt = 0; attempt < 50 && !locked; attempt++)
            {
                await using var look = databases.Orders();
                locked = await look.Set<OutboxMessage>().AsNoTracking().AnyAsync(row =>
                    row.Id == message.Id && row.LockToken != null && row.PublishedAt == null);
                if (!locked)
                {
                    await Task.Delay(20);
                }
            }

            Assert.True(locked, "The first dispatcher did not claim the outbox row.");
            var secondPublished = await dispatcher.DispatchBatchAsync(secondDb, secondBroker, options, CancellationToken.None);
            hold.TrySetResult();
            var firstPublished = await first;

            Assert.Equal(1, firstPublished);
            Assert.Equal(1, firstBroker.Publishes);
            Assert.Equal(0, secondPublished);
            Assert.DoesNotContain(secondBroker.Messages, row => row.Id == message.Id);
            await using var check = databases.Orders();
            var stored = await check.Set<OutboxMessage>().AsNoTracking().SingleAsync(row => row.Id == message.Id);
            Assert.NotNull(stored.PublishedAt);
            Console.WriteLine($"OUTBOX_CLAIM id={message.Id} firstPublished={firstPublished} secondPublished={secondPublished} brokerPublishes={firstBroker.Publishes} publishedAt={stored.PublishedAt:o}");
        }
        finally
        {
            hold.TrySetResult();
            await using var cleanup = databases.Orders();
            await cleanup.Set<OutboxMessage>()
                .Where(row => row.Id != message.Id && row.PublishedAt == null && row.LockToken == null && row.LockedUntil == parkedUntil)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LockedUntil, (DateTimeOffset?)null));
        }
    }

    [SqlFact]
    public async Task Duplicate_inventory_and_payment_events_do_not_repeat_side_effects()
    {
        await using var inventoryDb = databases.Inventory();
        var before = (await inventoryDb.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.Widget)).AvailableQuantity;
        var orderId = Guid.NewGuid();
        var reserve = Envelope(EventTypes.ReserveInventory, orderId, IntegrationJson.Serialize(new ReserveInventoryV1(orderId, [new OrderLineV1(DemoCatalog.Widget, 1)])));

        await using (var first = databases.Inventory())
        {
            await new InventoryMessageHandler(new InventoryApplication(first), first, NullLogger<InventoryMessageHandler>.Instance)
                .HandleAsync(reserve, CancellationToken.None);
        }

        await using (var second = databases.Inventory())
        {
            await new InventoryMessageHandler(new InventoryApplication(second), second, NullLogger<InventoryMessageHandler>.Instance)
                .HandleAsync(reserve, CancellationToken.None);
        }

        await using var afterDb = databases.Inventory();
        var after = (await afterDb.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.Widget)).AvailableQuantity;
        Assert.Equal(before - 1, after);
        Assert.Equal(1, await afterDb.Reservations.CountAsync(row => row.OrderId == orderId));

        var authorize = Envelope(EventTypes.AuthorizePayment, orderId, IntegrationJson.Serialize(new AuthorizePaymentV1(orderId, 15m, "authorize")));
        await using (var payments = databases.Payments())
        {
            var handler = new PaymentMessageHandler(new PaymentApplication(payments), payments, NullLogger<PaymentMessageHandler>.Instance);
            await handler.HandleAsync(authorize, CancellationToken.None);
            await handler.HandleAsync(authorize, CancellationToken.None);
            Assert.Equal(1, await payments.Payments.CountAsync(row => row.OrderId == orderId));
        }
    }

    [SqlFact]
    public async Task Late_and_out_of_order_events_do_not_change_a_terminal_or_early_order()
    {
        var confirmed = await PlaceOrderAsync("authorize", DemoCatalog.Widget, 1);
        await PumpAsync(confirmed.Id);
        await using (var orders = databases.Orders())
        {
            var stored = await orders.Orders.AsNoTracking().SingleAsync(row => row.Id == confirmed.Id);
            var late = Envelope(EventTypes.InventoryReserved, confirmed.Id, IntegrationJson.Serialize(new InventoryReservedV1(confirmed.Id, stored.ReservationId!.Value)));
            await using var handlerDb = databases.Orders();
            await new OrderWorkflowHandler(handlerDb, NullLogger<OrderWorkflowHandler>.Instance).HandleAsync(late, CancellationToken.None);
            var reread = await handlerDb.Orders.SingleAsync(row => row.Id == confirmed.Id);
            Assert.Equal(OrderStatus.Confirmed, reread.Status);
            Assert.Contains(await handlerDb.WorkflowIncidents.Where(row => row.OrderId == confirmed.Id).Select(row => row.Outcome).ToListAsync(), outcome => outcome == WorkflowApplicationResult.Duplicate.ToString() || outcome == WorkflowApplicationResult.IgnoredLate.ToString());
        }

        var early = await PlaceOrderAsync("authorize", DemoCatalog.Widget, 1);
        var paymentEvent = Envelope(EventTypes.PaymentAuthorized, early.Id, IntegrationJson.Serialize(new PaymentAuthorizedV1(early.Id, Guid.NewGuid(), 12m)));
        await using var earlyDb = databases.Orders();
        await new OrderWorkflowHandler(earlyDb, NullLogger<OrderWorkflowHandler>.Instance).HandleAsync(paymentEvent, CancellationToken.None);
        var pending = await earlyDb.Orders.SingleAsync(row => row.Id == early.Id);
        Assert.Equal(OrderStatus.Pending, pending.Status);
        Assert.Equal(WorkflowApplicationResult.IgnoredLate.ToString(), (await earlyDb.WorkflowIncidents.SingleAsync(row => row.EventId == paymentEvent.EventId)).Outcome);
    }

    private async Task<Order> SaveCompensationFailedAsync(Guid causationId)
    {
        await using var db = databases.Orders();
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "retry-" + Guid.NewGuid().ToString("N"),
            CustomerId = "customer-1",
            PaymentSimulation = "decline",
            Currency = "USD",
            Status = OrderStatus.CompensationFailed,
            ReservationId = Guid.NewGuid(),
            FailureReason = "simulated",
            TotalAmount = 12m,
            CreatedAt = now,
            UpdatedAt = now,
            Transitions =
            [
                new OrderTransition
                {
                    FromStatus = OrderStatus.CompensationPending,
                    ToStatus = OrderStatus.CompensationFailed,
                    Trigger = "InventoryReleaseFailed",
                    EventId = causationId,
                    OccurredAt = now
                }
            ]
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Order> PlaceOrderAsync(string simulation, string productId, int quantity)
    {
        await using var db = databases.Orders();
        var result = await new OrderApplication(db).CreateAsync(
            new CreateOrder("customer-1", simulation, "USD", [new CreateOrderLine(productId, quantity, 12m)]),
            Guid.NewGuid().ToString("N"),
            CancellationToken.None);
        return result.Order;
    }

    private async Task PumpAsync(Guid orderId)
    {
        for (var step = 0; step < 12; step++)
        {
            var batch = new List<OutboxMessage>();
            await using (var orders = databases.Orders())
            {
                batch.AddRange(await TakeUnpublishedAsync(orders, orderId));
            }

            await using (var inventory = databases.Inventory())
            {
                batch.AddRange(await TakeUnpublishedAsync(inventory, orderId));
            }

            await using (var payments = databases.Payments())
            {
                batch.AddRange(await TakeUnpublishedAsync(payments, orderId));
            }
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var message in batch)
            {
                var envelope = IntegrationJson.DeserializeEnvelope(Encoding.UTF8.GetBytes(message.Payload));
                await RouteAsync(envelope);
            }
        }

        throw new InvalidOperationException($"The workflow for {orderId} did not drain the outbox.");
    }

    private static async Task<List<OutboxMessage>> TakeUnpublishedAsync(DbContext db, Guid orderId)
    {
        var rows = await db.Set<OutboxMessage>()
            .Where(message => message.CorrelationId == orderId && message.PublishedAt == null)
            .ToListAsync();
        if (rows.Count == 0)
        {
            return rows;
        }

        foreach (var row in rows)
        {
            row.PublishedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return rows;
    }

    private async Task RouteAsync(IntegrationEventEnvelope envelope)
    {
        switch (envelope.EventType)
        {
            case EventTypes.ReserveInventory:
            case EventTypes.ReleaseInventory:
                await using (var db = databases.Inventory())
                {
                    await new InventoryMessageHandler(new InventoryApplication(db), db, NullLogger<InventoryMessageHandler>.Instance)
                        .HandleAsync(envelope, CancellationToken.None);
                }

                break;
            case EventTypes.AuthorizePayment:
                await using (var db = databases.Payments())
                {
                    await new PaymentMessageHandler(new PaymentApplication(db), db, NullLogger<PaymentMessageHandler>.Instance)
                        .HandleAsync(envelope, CancellationToken.None);
                }

                break;
            case EventTypes.InventoryReserved:
            case EventTypes.InventoryReservationFailed:
            case EventTypes.InventoryReleased:
            case EventTypes.InventoryReleaseFailed:
            case EventTypes.PaymentAuthorized:
            case EventTypes.PaymentFailed:
                await using (var db = databases.Orders())
                {
                    await new OrderWorkflowHandler(db, NullLogger<OrderWorkflowHandler>.Instance).HandleAsync(envelope, CancellationToken.None);
                }

                break;
            case EventTypes.OrderPlaced:
            case EventTypes.OrderConfirmed:
            case EventTypes.OrderFailed:
            case EventTypes.OrderCompensationFailed:
                await using (var db = databases.Notifications())
                {
                    await new NotificationMessageHandler(db, NullLogger<NotificationMessageHandler>.Instance).HandleAsync(envelope, CancellationToken.None);
                }

                break;
            default:
                throw new InvalidOperationException($"No test route for {envelope.EventType}.");
        }
    }

    private async Task<int> UnpublishedForAsync(Guid orderId)
    {
        var count = 0;
        await using (var orders = databases.Orders())
        {
            count += await orders.Set<OutboxMessage>().CountAsync(message => message.CorrelationId == orderId && message.PublishedAt == null);
        }

        await using (var inventory = databases.Inventory())
        {
            count += await inventory.Set<OutboxMessage>().CountAsync(message => message.CorrelationId == orderId && message.PublishedAt == null);
        }

        await using (var payments = databases.Payments())
        {
            count += await payments.Set<OutboxMessage>().CountAsync(message => message.CorrelationId == orderId && message.PublishedAt == null);
        }

        return count;
    }

    private static Order NewPendingOrder() => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        CustomerId = "customer-1",
        PaymentSimulation = "authorize",
        Currency = "USD",
        Status = OrderStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static IntegrationEventEnvelope Envelope(string eventType, Guid orderId, string payload) => new()
    {
        EventId = Guid.NewGuid(),
        EventType = eventType,
        EventVersion = 1,
        CorrelationId = orderId,
        OccurredAt = DateTimeOffset.UtcNow,
        Payload = payload
    };
}

public sealed class HoldBroker(Guid messageId, Task hold) : IBrokerPublisher
{
    public int Publishes;

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.Id != messageId)
        {
            return;
        }

        Interlocked.Increment(ref Publishes);
        await hold.WaitAsync(cancellationToken);
    }
}

public sealed class RecordingBroker : IBrokerPublisher
{
    public List<OutboxMessage> Messages { get; } = [];

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class ThrowingBroker : IBrokerPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The broker refused the publish.");
}

public sealed class SqlDatabases : IAsyncLifetime
{
    public const string OrdersDatabase = "OrdersDb_It";
    public const string InventoryDatabase = "InventoryDb_It";
    public const string PaymentsDatabase = "PaymentsDb_It";
    public const string NotificationsDatabase = "NotificationsDb_It";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _ready;

    public OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDatabase));

    public InventoryDbContext Inventory() => new(Options<InventoryDbContext>(InventoryDatabase));

    public PaymentsDbContext Payments() => new(Options<PaymentsDbContext>(PaymentsDatabase));

    public NotificationsDbContext Notifications() => new(Options<NotificationsDbContext>(NotificationsDatabase));

    public Task InitializeAsync() => EnsureReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task EnsureReadyAsync()
    {
        if (!SqlServerGate.IsAvailable || _ready)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (_ready)
            {
                return;
            }

            await using (var orders = Orders())
            {
                await orders.Database.MigrateAsync();
            }

            await using (var inventory = Inventory())
            {
                await inventory.Database.MigrateAsync();
                await InventorySeed.SeedAsync(inventory, CancellationToken.None);
                var limited = await inventory.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.LimitedWidget);
                limited.AvailableQuantity = 25;
                limited.ReservedQuantity = 0;
                var release = await inventory.StockItems.SingleAsync(item => item.ProductId == DemoCatalog.ReleaseFails);
                if (release.AvailableQuantity < 5)
                {
                    release.AvailableQuantity = 10;
                }

                await inventory.SaveChangesAsync();
            }

            await using (var payments = Payments())
            {
                await payments.Database.MigrateAsync();
            }

            await using (var notifications = Notifications())
            {
                await notifications.Database.MigrateAsync();
            }

            _ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static DbContextOptions<TContext> Options<TContext>(string database)
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(SqlServerGate.DatabaseConnection(database))
            .Options;
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlDatabases>;
