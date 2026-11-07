using System.Diagnostics;
using Commerce.Contracts.Messaging;
using Commerce.Contracts.Telemetry;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Contracts.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public int EventVersion { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string Payload { get; set; } = "";
    public string? TraceParent { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public Guid? LockToken { get; set; }
    public string? LastError { get; set; }
}

public sealed class InboxMessage
{
    public Guid EventId { get; set; }
    public string ConsumerName { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

public static class MessagingModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.TraceParent).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(500);
            entity.HasIndex(x => new { x.PublishedAt, x.AttemptCount, x.OccurredAt });
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("InboxMessages");
            entity.HasKey(x => new { x.EventId, x.ConsumerName });
            entity.Property(x => x.ConsumerName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.EventId, x.ConsumerName }).IsUnique();
        });
    }
}

public static class SqlErrors
{
    public static bool IsUniqueViolation(DbUpdateException exception, string objectName)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Number is 2601 or 2627)
            {
                return sql.Message.Contains(objectName, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}

public static class OutboxWriter
{
    public static OutboxMessage Add<TPayload>(
        DbContext db,
        string eventType,
        int eventVersion,
        Guid correlationId,
        Guid? causationId,
        TPayload payload)
    {
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var envelope = new IntegrationEventEnvelope
        {
            EventId = eventId,
            EventType = eventType,
            EventVersion = eventVersion,
            CorrelationId = correlationId,
            CausationId = causationId,
            OccurredAt = occurredAt,
            Payload = IntegrationJson.Serialize(payload)
        };

        var message = new OutboxMessage
        {
            Id = eventId,
            EventType = eventType,
            EventVersion = eventVersion,
            CorrelationId = correlationId,
            CausationId = causationId,
            OccurredAt = occurredAt,
            Payload = IntegrationJson.Serialize(envelope),
            TraceParent = Activity.Current?.Id
        };

        db.Set<OutboxMessage>().Add(message);
        return message;
    }
}

public static class InboxTransaction
{
    public static async Task RunAsync(
        DbContext db,
        string consumerName,
        IntegrationEventEnvelope envelope,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Set<InboxMessage>().Add(new InboxMessage
        {
            EventId = envelope.EventId,
            ConsumerName = consumerName,
            EventType = envelope.EventType,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolation(exception, "InboxMessages"))
        {
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        await work(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public interface IBrokerPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class OutboxDispatcher(ILogger<OutboxDispatcher> logger)
{
    public async Task<int> DispatchBatchAsync(
        DbContext db,
        IBrokerPublisher broker,
        OutboxOptions options,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var candidateIds = await db.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(message => message.PublishedAt == null
                && message.AttemptCount < options.MaxAttempts
                && (message.LockedUntil == null || message.LockedUntil < now))
            .OrderBy(message => message.OccurredAt)
            .Take(options.BatchSize)
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var id in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = Guid.NewGuid();
            var lockedUntil = now.AddSeconds(options.LockSeconds);
            var claimed = await db.Set<OutboxMessage>()
                .Where(message => message.Id == id
                    && message.PublishedAt == null
                    && (message.LockedUntil == null || message.LockedUntil < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.LockToken, token)
                    .SetProperty(message => message.LockedUntil, lockedUntil), cancellationToken);

            if (claimed == 0)
            {
                continue;
            }

            var message = await db.Set<OutboxMessage>().AsNoTracking().FirstAsync(row => row.Id == id, cancellationToken);
            try
            {
                await broker.PublishAsync(message, cancellationToken);
                var marked = await db.Set<OutboxMessage>()
                    .Where(row => row.Id == id && row.LockToken == token && row.PublishedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.PublishedAt, DateTimeOffset.UtcNow)
                        .SetProperty(row => row.LockedUntil, (DateTimeOffset?)null)
                        .SetProperty(row => row.LockToken, (Guid?)null)
                        .SetProperty(row => row.LastError, (string?)null), cancellationToken);

                if (marked == 1)
                {
                    published++;
                }
                else
                {
                    logger.LogWarning(
                        "Event {EventId} was handed to the broker, but the outbox lock was no longer held. A duplicate publication is possible.",
                        id);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var error = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
                await db.Set<OutboxMessage>()
                    .Where(row => row.Id == id && row.LockToken == token)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.AttemptCount, row => row.AttemptCount + 1)
                        .SetProperty(row => row.LockedUntil, (DateTimeOffset?)null)
                        .SetProperty(row => row.LockToken, (Guid?)null)
                        .SetProperty(row => row.LastError, error), cancellationToken);

                MessagingTelemetry.PublishRetries.Add(1, new KeyValuePair<string, object?>("event.type", message.EventType));
                logger.LogWarning(exception, "Outbox publication failed for {EventId}. It will be retried until attempt {MaxAttempts}.", id, options.MaxAttempts);
            }
        }

        return published;
    }
}

public sealed class OutboxPublisherHostedService<TContext>(
    IServiceScopeFactory scopes,
    OutboxDispatcher dispatcher,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPublisherHostedService<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox publisher for {Context} started", typeof(TContext).Name);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();
                var broker = scope.ServiceProvider.GetRequiredService<IBrokerPublisher>();
                await dispatcher.DispatchBatchAsync(db, broker, options.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox publisher loop failed for {Context}", typeof(TContext).Name);
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Outbox publisher for {Context} stopped", typeof(TContext).Name);
    }
}
