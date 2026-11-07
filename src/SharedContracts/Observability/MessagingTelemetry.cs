using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Commerce.Contracts.Telemetry;

public static class MessagingTelemetry
{
    public const string ActivitySourceName = "Commerce.Messaging";
    public const string MeterName = "Commerce.Messaging";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> PublishRetries = Meter.CreateCounter<long>(
        "commerce.outbox.publish.retries",
        description: "Outbox publication attempts that failed and will be retried.");

    public static readonly Counter<long> OutboxExhausted = Meter.CreateCounter<long>(
        "commerce.outbox.publish.exhausted",
        description: "Outbox records that reached the maximum publication attempts.");

    public static readonly Counter<long> MessagesProcessed = Meter.CreateCounter<long>(
        "commerce.consumer.processed",
        description: "Integration events acknowledged after a successful local transaction.");

    public static readonly Counter<long> ConsumerRetries = Meter.CreateCounter<long>(
        "commerce.consumer.retries",
        description: "Consumer failures that were republished for another attempt.");

    public static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>(
        "commerce.consumer.dead_letters",
        description: "Messages sent to a dead-letter queue after the retry limit.");
}
