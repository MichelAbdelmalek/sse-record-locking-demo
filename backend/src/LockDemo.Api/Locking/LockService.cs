using LockDemo.Api.Data;
using LockDemo.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace LockDemo.Api.Locking;

public sealed class LockOptions
{
    /// <summary>A tab counts as connected while its last heartbeat is newer than this.</summary>
    public int TtlSeconds { get; set; } = 30;
    public int HeartbeatSeconds { get; set; } = 10;
    public int SweepSeconds { get; set; } = 15;
    public TimeSpan Ttl => TimeSpan.FromSeconds(TtlSeconds);
}

public sealed record LockDto(Guid HolderPublicId, string HolderName, DateTimeOffset AcquiredAt);

public sealed record RecordDto(
    int Id, string Reference, string Title, string Status, int Priority, string Notes,
    DateTimeOffset UpdatedAt, string? UpdatedBy, uint Version, LockDto? Lock);

public sealed record RecordUpdate(string Title, string Status, string Notes, uint Version);

public enum LockOutcome { Acquired, HeldByOther, NotConnected, NotFound }
public enum SaveOutcome { Saved, NotLockHolder, VersionConflict, NotFound }

public sealed record LockResult(LockOutcome Outcome, LockDto? Lock = null);
public sealed record SaveResult(SaveOutcome Outcome, RecordDto? Record = null);

public sealed class LockService(AppDbContext db, ILockEventBus bus, TimeProvider clock, Microsoft.Extensions.Options.IOptions<LockOptions> options)
{
    private readonly LockOptions _opt = options.Value;
    private DateTimeOffset Cutoff => clock.GetUtcNow() - _opt.Ttl;

    /// <summary>Records with their lock, showing a lock only while its holder is still connected.</summary>
    public IQueryable<RecordDto> Project(IQueryable<WorkRecord> source)
    {
        var cutoff = Cutoff;
        return source.Select(r => new RecordDto(
            r.Id, r.Reference, r.Title, r.Status.ToString(), r.Priority, r.Notes, r.UpdatedAt, r.UpdatedBy, r.Version,
            db.TabSessions
                .Where(s => r.Lock != null && s.TabId == r.Lock.HolderTabId && s.LastSeen > cutoff)
                .Select(s => new LockDto(s.PublicId, r.Lock!.HolderName, r.Lock.AcquiredAt))
                .FirstOrDefault()));
    }

    /// <summary>
    /// Next record in queue order after the cursor whose lock is free or stale.
    /// One keyset query: filters and lock status are applied in the database, no paging in memory.
    /// </summary>
    public Task<RecordDto?> NextUnlockedAsync(int afterPriority, int afterId, CancellationToken ct)
    {
        var cutoff = Cutoff;
        var query = db.Records
            .Where(r => r.Status == RecordStatus.Pending || r.Status == RecordStatus.InReview)
            .Where(r => r.Priority > afterPriority || (r.Priority == afterPriority && r.Id > afterId))
            .Where(r => r.Lock == null || !db.TabSessions.Any(s => s.TabId == r.Lock.HolderTabId && s.LastSeen > cutoff))
            .OrderBy(r => r.Priority).ThenBy(r => r.Id);
        return Project(query).FirstOrDefaultAsync(ct);
    }

    public async Task<LockResult> AcquireAsync(int recordId, Guid tabId, bool takeover, CancellationToken ct)
    {
        if (!await db.Records.AnyAsync(r => r.Id == recordId, ct)) return new(LockOutcome.NotFound);

        var cutoff = Cutoff;
        var me = await db.TabSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TabId == tabId && s.LastSeen > cutoff, ct);
        if (me is null) return new(LockOutcome.NotConnected);

        var now = clock.GetUtcNow();
        Guid? displaced = null;

        if (takeover)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var previous = (await db.RecordLocks
                .FromSql($"SELECT * FROM record_locks WHERE record_id = {recordId} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct)).FirstOrDefault();
            await UpsertAsync(recordId, me, now, onlyIfFree: false, cutoff, ct);
            await tx.CommitAsync(ct);
            if (previous is not null && previous.HolderTabId != tabId) displaced = previous.HolderTabId;
        }
        else
        {
            // Atomic: take the lock if it is free, already ours, or its holder is no longer connected.
            var won = await UpsertAsync(recordId, me, now, onlyIfFree: true, cutoff, ct);
            if (!won)
            {
                var current = await Project(db.Records.Where(r => r.Id == recordId)).Select(r => r.Lock).FirstOrDefaultAsync(ct);
                return new(LockOutcome.HeldByOther, current);
            }
        }

        var dto = new LockDto(me.PublicId, me.Name, now);
        if (displaced is { } loser)
            await bus.PublishAsync(LockEvent.Create(LockEvent.LockLost, recordId, new { byName = me.Name }, loser), ct);
        await bus.PublishAsync(LockEvent.Create(LockEvent.LockChanged, recordId, dto), ct);
        return new(LockOutcome.Acquired, dto);
    }

    private async Task<bool> UpsertAsync(int recordId, TabSession me, DateTimeOffset now, bool onlyIfFree, DateTimeOffset cutoff, CancellationToken ct)
    {
        var rows = onlyIfFree
            ? await db.Database.SqlQuery<Guid>($"""
                INSERT INTO record_locks (record_id, holder_tab_id, holder_name, acquired_at)
                VALUES ({recordId}, {me.TabId}, {me.Name}, {now})
                ON CONFLICT (record_id) DO UPDATE
                   SET holder_tab_id = EXCLUDED.holder_tab_id,
                       holder_name   = EXCLUDED.holder_name,
                       acquired_at   = EXCLUDED.acquired_at
                 WHERE record_locks.holder_tab_id = EXCLUDED.holder_tab_id
                    OR NOT EXISTS (SELECT 1 FROM tab_sessions s
                                    WHERE s.tab_id = record_locks.holder_tab_id AND s.last_seen > {cutoff})
                RETURNING holder_tab_id AS "Value"
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<Guid>($"""
                INSERT INTO record_locks (record_id, holder_tab_id, holder_name, acquired_at)
                VALUES ({recordId}, {me.TabId}, {me.Name}, {now})
                ON CONFLICT (record_id) DO UPDATE
                   SET holder_tab_id = EXCLUDED.holder_tab_id,
                       holder_name   = EXCLUDED.holder_name,
                       acquired_at   = EXCLUDED.acquired_at
                RETURNING holder_tab_id AS "Value"
                """).ToListAsync(ct);
        return rows.Count == 1;
    }

    public async Task ReleaseAsync(int recordId, Guid tabId, CancellationToken ct)
    {
        var removed = await db.RecordLocks
            .Where(l => l.RecordId == recordId && l.HolderTabId == tabId)
            .ExecuteDeleteAsync(ct);
        if (removed > 0)
            await bus.PublishAsync(LockEvent.Create<LockDto?>(LockEvent.LockChanged, recordId, null), ct);
    }

    /// <summary>
    /// The save is validated against the current lock holder inside the same transaction
    /// (the lock row is locked FOR UPDATE), so a tab that lost its lock can never write.
    /// </summary>
    public async Task<SaveResult> SaveAsync(int recordId, Guid tabId, RecordUpdate update, CancellationToken ct)
    {
        var status = Enum.Parse<RecordStatus>(update.Status, ignoreCase: true);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var heldLock = (await db.RecordLocks
            .FromSql($"SELECT * FROM record_locks WHERE record_id = {recordId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct)).FirstOrDefault();
        var cutoff = Cutoff;
        var holderLive = heldLock is not null && heldLock.HolderTabId == tabId &&
                         await db.TabSessions.AnyAsync(s => s.TabId == tabId && s.LastSeen > cutoff, ct);
        if (!holderLive) return new(SaveOutcome.NotLockHolder);

        var record = await db.Records.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return new(SaveOutcome.NotFound);

        db.Entry(record).Property(r => r.Version).OriginalValue = update.Version;
        record.Title = update.Title.Trim();
        record.Status = status;
        record.Notes = update.Notes.Trim();
        record.UpdatedAt = clock.GetUtcNow();
        record.UpdatedBy = heldLock!.HolderName;

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var latest = await Project(db.Records.Where(r => r.Id == recordId)).FirstOrDefaultAsync(ct);
            return new(SaveOutcome.VersionConflict, latest);
        }
        await tx.CommitAsync(ct);

        var dto = await Project(db.Records.AsNoTracking().Where(r => r.Id == recordId)).FirstAsync(ct);
        await bus.PublishAsync(LockEvent.Create(LockEvent.RecordUpdated, recordId, dto), ct);
        return new(SaveOutcome.Saved, dto);
    }
}
