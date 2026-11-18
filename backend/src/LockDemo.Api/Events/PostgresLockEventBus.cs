using System.Text.Json;
using Npgsql;

namespace LockDemo.Api.Events;

/// <summary>
/// Several API instances: every event goes through Postgres NOTIFY, and each instance
/// LISTENs and dispatches to the tabs connected to it. A tab whose lock is taken over
/// from another instance still gets its warning.
/// Needs a direct (non-pooled) connection string: PgBouncer in transaction mode drops LISTEN.
/// </summary>
public sealed class PostgresLockEventBus(NpgsqlDataSource dataSource) : ILockEventBus
{
    public const string Channel = "lock_events";

    public async Task PublishAsync(LockEvent evt, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(evt, JsonSerializerOptions.Web);
        await using var cmd = dataSource.CreateCommand("SELECT pg_notify(@channel, @payload)");
        cmd.Parameters.AddWithValue("channel", Channel);
        cmd.Parameters.AddWithValue("payload", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class PostgresLockEventListener(
    NpgsqlDataSource dataSource, TabRegistry registry, ILogger<PostgresLockEventListener> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, e) =>
                {
                    try
                    {
                        var evt = JsonSerializer.Deserialize<LockEvent>(e.Payload, JsonSerializerOptions.Web);
                        if (evt is not null) registry.Dispatch(evt);
                    }
                    catch (JsonException ex) { log.LogWarning(ex, "Ignoring malformed lock event"); }
                };
                await using (var listen = new NpgsqlCommand($"LISTEN {PostgresLockEventBus.Channel}", conn))
                    await listen.ExecuteNonQueryAsync(stoppingToken);

                log.LogInformation("Listening for lock events on Postgres");
                while (!stoppingToken.IsCancellationRequested)
                    await conn.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Lock event listener dropped; reconnecting in 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
