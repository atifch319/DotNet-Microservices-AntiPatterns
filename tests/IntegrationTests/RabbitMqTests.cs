using System.Text;
using Commerce.Contracts.Messaging;
using RabbitMQ.Client;

namespace IntegrationTests;

public class RabbitMqTests
{
    [RabbitFact]
    public async Task A_durable_message_can_be_published_and_read_back()
    {
        var factory = new ConnectionFactory
        {
            HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out var port) ? port : 5672,
            UserName = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest",
            Password = Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "guest",
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };

        await using var connection = await factory.CreateConnectionAsync();
        var options = new CreateChannelOptions(true, true, null, 1);
        await using var channel = await connection.CreateChannelAsync(options);
        await Topology.DeclareAsync(channel, CancellationToken.None);

        var body = Encoding.UTF8.GetBytes("{\"eventType\":\"order.placed.v1\"}");
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Persistent = true,
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = Guid.NewGuid().ToString(),
            Type = "order.placed.v1"
        };

        await channel.BasicPublishAsync(Topology.Exchange, "order.placed.v1", mandatory: true, properties, body);
        var delivered = await channel.BasicGetAsync(Topology.NotificationQueue, autoAck: false);
        Assert.NotNull(delivered);
        await channel.BasicAckAsync(delivered.DeliveryTag, multiple: false);
        Assert.Equal(body, delivered.Body.ToArray());
    }
}
