using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commerce.Contracts.Messaging;

public sealed class IntegrationEventEnvelope
{
    public Guid EventId { get; init; }
    public string EventType { get; init; } = "";
    public int EventVersion { get; init; }
    public Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string Payload { get; init; } = "";
}

public static class IntegrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T DeserializePayload<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");

    public static IntegrationEventEnvelope DeserializeEnvelope(ReadOnlySpan<byte> body)
    {
        var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(body, Options);
        return envelope ?? throw new InvalidOperationException("Could not deserialize the integration event envelope.");
    }
}

public static class ConsumerNames
{
    public const string OrderWorkflow = "order-workflow";
    public const string Inventory = "inventory";
    public const string Payment = "payment";
    public const string Notification = "notification";
}

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string ClientName { get; set; } = "commerce-service";
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";
    public int PollIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 20;
    public int LockSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 10;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";
    public int MaxDeliveryAttempts { get; set; } = 5;
}

public enum DeliveryDisposition
{
    Ack,
    Republish,
    DeadLetter
}

/// <summary>
/// Decides whether a failed delivery is retried or dead-lettered. There is no infinite retry.
/// </summary>
public static class DeliveryPolicy
{
    public static DeliveryDisposition Decide(int attempt, int maxAttempts, bool succeeded)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (succeeded)
        {
            return DeliveryDisposition.Ack;
        }

        return attempt + 1 >= maxAttempts
            ? DeliveryDisposition.DeadLetter
            : DeliveryDisposition.Republish;
    }
}
