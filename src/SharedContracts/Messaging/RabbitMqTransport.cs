using System.Diagnostics;
using System.Text;
using Commerce.Contracts.Persistence;
using Commerce.Contracts.Telemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Commerce.Contracts.Messaging;

public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var settings = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.Host,
                Port = settings.Port,
                UserName = settings.UserName,
                Password = settings.Password,
                VirtualHost = settings.VirtualHost,
                ClientProvidedName = settings.ClientName,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}

public sealed class RabbitMqBrokerPublisher(RabbitMqConnection connections, ILogger<RabbitMqBrokerPublisher> logger) : IBrokerPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var connection = await connections.GetAsync(cancellationToken);
        var channelOptions = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true, outstandingPublisherConfirmationsRateLimiter: null, consumerDispatchConcurrency: 1);
        await using var channel = await connection.CreateChannelAsync(channelOptions, cancellationToken);
        await Topology.DeclareAsync(channel, cancellationToken);

        var headers = new Dictionary<string, object?>
        {
            ["event-type"] = message.EventType,
            ["event-version"] = message.EventVersion,
            ["x-attempt"] = 0
        };

        if (!string.IsNullOrWhiteSpace(message.TraceParent))
        {
            headers["traceparent"] = message.TraceParent;
        }

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Persistent = true,
            MessageId = message.Id.ToString(),
            CorrelationId = message.CorrelationId.ToString(),
            Type = message.EventType,
            Headers = headers
        };

        using var activity = MessagingTelemetry.Source.StartActivity("publish " + message.EventType, ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", Topology.Exchange);
        activity?.SetTag("messaging.rabbitmq.message.routing_key", message.EventType);
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("correlation.id", message.CorrelationId.ToString());

        var body = Encoding.UTF8.GetBytes(message.Payload);
        await channel.BasicPublishAsync(Topology.Exchange, message.EventType, mandatory: true, properties, body, cancellationToken);
        logger.LogInformation("Published {EventType} {EventId} correlated to {CorrelationId}", message.EventType, message.Id, message.CorrelationId);
    }
}

public interface IIntegrationEventHandler
{
    Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class RabbitConsumerWorker(
    string queueName,
    Type handlerType,
    IServiceScopeFactory scopes,
    RabbitMqConnection connections,
    IOptions<MessagingOptions> options,
    ILogger<RabbitConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RabbitMQ consumer for {Queue} disconnected. It will reconnect.", queueName);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var connection = await connections.GetAsync(stoppingToken);
        var channelOptions = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true, outstandingPublisherConfirmationsRateLimiter: null, consumerDispatchConcurrency: 1);
        await using var channel = await connection.CreateChannelAsync(channelOptions, stoppingToken);
        await Topology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);

        var consumerTag = await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Consuming {Queue} as {ConsumerTag}", queueName, consumerTag);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await channel.BasicCancelAsync(consumerTag, noWait: false, cancellationToken: CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Consumer {Queue} cancel during shutdown did not complete cleanly", queueName);
            }
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        var attempt = HeaderValues.ReadAttempt(delivery.BasicProperties.Headers);
        using var activity = StartActivity(delivery);
        try
        {
            var envelope = IntegrationJson.DeserializeEnvelope(delivery.Body.Span);
            activity?.SetTag("messaging.message.id", envelope.EventId);
            activity?.SetTag("correlation.id", envelope.CorrelationId);

            await using var scope = scopes.CreateAsyncScope();
            var handler = (IIntegrationEventHandler)scope.ServiceProvider.GetRequiredService(handlerType);
            await handler.HandleAsync(envelope, cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            MessagingTelemetry.MessagesProcessed.Add(1, new KeyValuePair<string, object?>("queue", queueName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Processing failed on {Queue} for message {MessageId} at attempt {Attempt}", queueName, delivery.BasicProperties.MessageId, attempt);
            var disposition = DeliveryPolicy.Decide(attempt, options.Value.MaxDeliveryAttempts, succeeded: false);
            if (disposition == DeliveryDisposition.DeadLetter)
            {
                MessagingTelemetry.DeadLetters.Add(1, new KeyValuePair<string, object?>("queue", queueName));
                logger.LogError("Dead-lettering message {MessageId} from {Queue} after {MaxAttempts} attempts", delivery.BasicProperties.MessageId, queueName, options.Value.MaxDeliveryAttempts);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken);
                return;
            }

            await RepublishAsync(channel, delivery, attempt + 1, cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            MessagingTelemetry.ConsumerRetries.Add(1, new KeyValuePair<string, object?>("queue", queueName));
        }
    }

    private static Activity? StartActivity(BasicDeliverEventArgs delivery)
    {
        ActivityContext parent = default;
        if (delivery.BasicProperties.Headers is not null
            && delivery.BasicProperties.Headers.TryGetValue("traceparent", out var raw)
            && HeaderValues.AsString(raw) is { Length: > 0 } traceParent
            && ActivityContext.TryParse(traceParent, null, out var parsed))
        {
            parent = parsed;
        }

        return MessagingTelemetry.Source.StartActivity("consume " + delivery.RoutingKey, ActivityKind.Consumer, parent);
    }

    private async Task RepublishAsync(IChannel channel, BasicDeliverEventArgs delivery, int nextAttempt, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, object?>();
        if (delivery.BasicProperties.Headers is not null)
        {
            foreach (var header in delivery.BasicProperties.Headers)
            {
                headers[header.Key] = header.Value;
            }
        }

        headers["x-attempt"] = nextAttempt;
        var properties = new BasicProperties
        {
            ContentType = delivery.BasicProperties.ContentType ?? "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Persistent = true,
            MessageId = delivery.BasicProperties.MessageId,
            CorrelationId = delivery.BasicProperties.CorrelationId,
            Type = delivery.BasicProperties.Type,
            Headers = headers
        };

        await channel.BasicPublishAsync(delivery.Exchange, delivery.RoutingKey, mandatory: true, properties, delivery.Body, cancellationToken);
    }
}

public static class HeaderValues
{
    public static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-attempt", out var value))
        {
            return 0;
        }

        return value switch
        {
            int number => number,
            long number => (int)number,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => 0
        };
    }

    public static string? AsString(object? value) => value switch
    {
        string text => text,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => null
    };
}

public sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await connection.GetAsync(cancellationToken);
            return current.IsOpen
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("RabbitMQ connection is open.")
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("RabbitMQ connection is closed.");
        }
        catch (Exception exception)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("RabbitMQ is not reachable.", exception);
        }
    }
}
