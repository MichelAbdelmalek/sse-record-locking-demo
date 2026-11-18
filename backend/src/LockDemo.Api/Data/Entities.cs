using System.ComponentModel.DataAnnotations;

namespace LockDemo.Api.Data;

public enum RecordStatus { Pending, InReview, Approved, Rejected }

/// <summary>A document in a review queue. Only the tab holding its lock may save it.</summary>
public class WorkRecord
{
    public int Id { get; set; }
    public required string Reference { get; set; }
    public required string Title { get; set; }
    public RecordStatus Status { get; set; }

    /// <summary>1 = highest. The queue is ordered by (Priority, Id), which the keyset query relies on.</summary>
    public int Priority { get; set; }

    public string Notes { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Maps to Postgres xmin: optimistic concurrency on top of the lock.</summary>
    [Timestamp]
    public uint Version { get; set; }

    public RecordLock? Lock { get; set; }
}

/// <summary>At most one lock per record, owned by one browser tab.</summary>
public class RecordLock
{
    public int RecordId { get; set; }

    /// <summary>The holder's secret tab id. Never sent to other clients.</summary>
    public Guid HolderTabId { get; set; }

    public required string HolderName { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }

    public WorkRecord? Record { get; set; }
}

/// <summary>
/// One row per connected tab. LastSeen is refreshed while the tab's SSE stream is open,
/// so a lock is only valid while its holder is actually connected.
/// </summary>
public class TabSession
{
    public Guid TabId { get; set; }

    /// <summary>Safe to share: lets a client recognise its own locks without exposing TabId.</summary>
    public Guid PublicId { get; set; }

    public required string Name { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
