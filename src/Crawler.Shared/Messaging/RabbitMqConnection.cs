using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace Crawler.Shared.Messaging;

// Connections are long-lived; each publisher/consumer owns its own channel.
public sealed class RabbitMqConnection(IConfiguration configuration) : IAsyncDisposable
{
    // This gate synchronizes threads inside one process; database locks separately coordinate different workers.
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;
    public bool IsConnected => connection?.IsOpen == true;

    /// <summary>
    /// Returns the current open connection or creates a replacement under a semaphore. The
    /// semaphore prevents concurrent consumers/dispatchers from racing to create multiple
    /// connections.
    /// </summary>
    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (connection?.IsOpen == true) return connection;
        await gate.WaitAsync(ct);
        try
        {
            if (connection?.IsOpen == true) return connection;
            if (connection is not null) await connection.DisposeAsync();
            var factory = new ConnectionFactory
            {
                Uri = new Uri(configuration["RabbitMq:Address"] ?? "amqp://localhost:5673/"),
                UserName = configuration["RabbitMq:Username"] ?? "crawler",
                Password = configuration["RabbitMq:Password"] ?? "crawler-local",
                // Our background loops explicitly reconnect and recreate channels after a five-second delay.
                AutomaticRecoveryEnabled = false,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
                ClientProvidedName = "crawler-" + Environment.MachineName
            };
            connection = await factory.CreateConnectionAsync(ct);
            return connection;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Releases the process-owned broker connection during host shutdown. Individual consumers and
    /// publishers dispose their own channels separately.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync();
        gate.Dispose();
    }
}
