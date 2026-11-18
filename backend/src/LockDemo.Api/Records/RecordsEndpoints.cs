using LockDemo.Api.Data;
using LockDemo.Api.Locking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LockDemo.Api.Records;

public sealed record RecordPage(IReadOnlyList<RecordDto> Items, Cursor? Next);
public sealed record Cursor(int Priority, int Id);
public sealed record LockRequest(bool Takeover);

public static class RecordsEndpoints
{
    /// <summary>The caller's per-tab id travels in this header on every write.</summary>
    public const string TabHeader = "X-Tab-Id";

    public static void MapRecords(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/records");

        // Keyset pagination over (priority, id): stable and index-backed, no OFFSET scans.
        api.MapGet("/", async (int? afterPriority, int? afterId, int? limit, LockService locks, AppDbContext db, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 25, 1, 100);
            var query = db.Records.AsNoTracking();
            if (afterPriority is { } p && afterId is { } id)
                query = query.Where(r => r.Priority > p || (r.Priority == p && r.Id > id));
            var items = await locks.Project(query.OrderBy(r => r.Priority).ThenBy(r => r.Id).Take(take + 1)).ToListAsync(ct);
            var next = items.Count > take ? new Cursor(items[take - 1].Priority, items[take - 1].Id) : null;
            return TypedResults.Ok(new RecordPage(items.Take(take).ToList(), next));
        });

        api.MapGet("/{id:int}", async Task<IResult> (int id, LockService locks, AppDbContext db, CancellationToken ct) =>
            await locks.Project(db.Records.AsNoTracking().Where(r => r.Id == id)).FirstOrDefaultAsync(ct) is { } rec
                ? TypedResults.Ok(rec) : TypedResults.NotFound());

        api.MapGet("/next-unlocked", async Task<IResult> (int? afterPriority, int? afterId, LockService locks, CancellationToken ct) =>
            await locks.NextUnlockedAsync(afterPriority ?? 0, afterId ?? 0, ct) is { } rec
                ? TypedResults.Ok(rec) : TypedResults.NoContent());

        api.MapPost("/{id:int}/lock", async Task<IResult> (int id, LockRequest? body, [FromHeader(Name = TabHeader)] Guid tabId,
            LockService locks, CancellationToken ct) =>
        {
            var result = await locks.AcquireAsync(id, tabId, body?.Takeover ?? false, ct);
            return result.Outcome switch
            {
                LockOutcome.Acquired => TypedResults.Ok(result.Lock),
                LockOutcome.HeldByOther => TypedResults.Conflict(new { error = "held", @lock = result.Lock }),
                LockOutcome.NotConnected => TypedResults.Problem(
                    "Open the live connection (/api/events) before taking locks.", statusCode: StatusCodes.Status428PreconditionRequired),
                _ => TypedResults.NotFound(),
            };
        }).RequireRateLimiting("writes");

        api.MapDelete("/{id:int}/lock", async (int id, [FromHeader(Name = TabHeader)] Guid tabId, LockService locks, CancellationToken ct) =>
        {
            await locks.ReleaseAsync(id, tabId, ct);
            return TypedResults.NoContent();
        }).RequireRateLimiting("writes");

        api.MapPut("/{id:int}", async Task<IResult> (int id, RecordUpdate body, [FromHeader(Name = TabHeader)] Guid tabId,
            LockService locks, CancellationToken ct) =>
        {
            if (Validation.Check(body) is { } errors) return TypedResults.ValidationProblem(errors);
            var result = await locks.SaveAsync(id, tabId, body, ct);
            return result.Outcome switch
            {
                SaveOutcome.Saved => TypedResults.Ok(result.Record),
                SaveOutcome.NotLockHolder => TypedResults.Problem(
                    "You no longer hold the lock on this record.", statusCode: StatusCodes.Status423Locked),
                SaveOutcome.VersionConflict => TypedResults.Conflict(new { error = "version", record = result.Record }),
                _ => TypedResults.NotFound(),
            };
        }).RequireRateLimiting("writes");
    }
}

public static class Validation
{
    public static string? CleanName(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) || n.Length > 40 ? null : n;
    }

    public static Dictionary<string, string[]>? Check(RecordUpdate u)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(u.Title) || u.Title.Trim().Length > 120) errors["title"] = ["Use 1 to 120 characters."];
        if (u.Notes is null || u.Notes.Length > 2000) errors["notes"] = ["Use at most 2000 characters."];
        if (!Enum.TryParse<RecordStatus>(u.Status, ignoreCase: true, out _) || int.TryParse(u.Status, out _))
            errors["status"] = ["Unknown status."];
        return errors.Count == 0 ? null : errors;
    }
}
