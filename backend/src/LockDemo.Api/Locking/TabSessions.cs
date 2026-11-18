using LockDemo.Api.Data;
using LockDemo.Api.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LockDemo.Api.Locking;

/// <summary>Opens and closes a tab's session row as its SSE stream connects and drops.</summary>
public sealed class TabSessionService(AppDbContext db, ILockEventBus bus, TimeProvider clock, IOptions<LockOptions> options)
{
    /// <summary>Upserts the session and returns its public id (stable across reconnects).</summary>
    public async Task<(Guid PublicId, DateTimeOffset ConnectedAt)> ConnectAsync(Guid tabId, string name, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var publicId = (await db.Database.SqlQuery<Guid>($"""
            INSERT INTO tab_sessions (tab_id, public_id, name, connected_at, last_seen)
            VALUES ({tabId}, {Guid.NewGuid()}, {name}, {now}, {now})
            ON CONFLICT (tab_id) DO UPDATE
               SET name = EXCLUDED.name, connected_at = EXCLUDED.connected_at, last_seen = EXCLUDED.last_seen
            RETURNING public_id AS "Value"
            """).ToListAsync(ct)).Single();
        return (publicId, now);
    }

    /// <summary>
    /// Ends a session unless the tab has reconnected since (on this or another instance),
    /// then frees its locks at once instead of waiting for the TTL.
    /// </summary>
    public async Task DisconnectAsync(Guid tabId, DateTimeOffset connectedAt, CancellationToken ct)
    {
        var ended = await db.TabSessions
            .Where(s => s.TabId == tabId && s.ConnectedAt == connectedAt)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeen, DateTimeOffset.UnixEpoch), ct);
        if (ended > 0) await ReleaseOrphanedLocksAsync(tabId, ct);
    }

    /// <summary>Deletes locks whose holder is no longer connected and tells every tab.</summary>
    public async Task<int> ReleaseOrphanedLocksAsync(Guid? onlyTab, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - options.Value.Ttl;
        var freed = await db.Database.SqlQuery<int>($"""
            DELETE FROM record_locks l
             WHERE ({onlyTab}::uuid IS NULL OR l.holder_tab_id = {onlyTab})
               AND NOT EXISTS (SELECT 1 FROM tab_sessions s
                                WHERE s.tab_id = l.holder_tab_id AND s.last_seen > {cutoff})
            RETURNING l.record_id AS "Value"
            """).ToListAsync(ct);
        foreach (var recordId in freed)
            await bus.PublishAsync(LockEvent.Create<LockDto?>(LockEvent.LockChanged, recordId, null), ct);
        return freed.Count;
    }
}

/// <summary>
/// Keeps connected tabs alive (heartbeat), and periodically frees locks whose holder
/// vanished without a clean disconnect, for example when an instance crashed.
/// </summary>
public sealed class LockMaintenanceService(
    IServiceScopeFactory scopes, TabRegistry registry, TimeProvider clock,
    IOptions<LockOptions> options, ILogger<LockMaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opt.HeartbeatSeconds), clock);
        var sinceSweep = TimeSpan.Zero;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var tabs = registry.ConnectedTabIds();
                if (tabs.Length > 0)
                {
                    var now = clock.GetUtcNow();
                    await db.TabSessions.Where(s => tabs.Contains(s.TabId))
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeen, now), stoppingToken);
                }

                sinceSweep += TimeSpan.FromSeconds(opt.HeartbeatSeconds);
                if (sinceSweep >= TimeSpan.FromSeconds(opt.SweepSeconds))
                {
                    sinceSweep = TimeSpan.Zero;
                    var sessions = scope.ServiceProvider.GetRequiredService<TabSessionService>();
                    var freed = await sessions.ReleaseOrphanedLocksAsync(null, stoppingToken);
                    if (freed > 0) log.LogInformation("Freed {Count} orphaned locks", freed);

                    var old = clock.GetUtcNow().AddHours(-1);
                    await db.TabSessions.Where(s => s.LastSeen < old).ExecuteDeleteAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Lock maintenance tick failed");
            }
        }
    }
}
