using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace IntegrationTests;

public static class SqlServerGate
{
    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    public static string MasterConnectionString { get; } =
        Environment.GetEnvironmentVariable("COMMERCE_SQL_MASTER")
        ?? "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=3";

    public static string DatabaseConnection(string database)
    {
        var builder = new SqlConnectionStringBuilder(MasterConnectionString)
        {
            InitialCatalog = database
        };
        return builder.ConnectionString;
    }

    private static bool Probe()
    {
        try
        {
            using var connection = new SqlConnection(MasterConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (!SqlServerGate.IsAvailable)
        {
            Skip = "SQL Server is not reachable. Set COMMERCE_SQL_MASTER to run this integration test. It was not executed.";
        }
    }
}

public static class RabbitGate
{
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        var host = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
        var port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out var parsed) ? parsed : 5672;
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(TimeSpan.FromMilliseconds(700)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class RabbitFactAttribute : FactAttribute
{
    public RabbitFactAttribute()
    {
        if (!RabbitGate.IsAvailable)
        {
            Skip = "RabbitMQ is not reachable on localhost:5672. This integration test was not executed.";
        }
    }
}
