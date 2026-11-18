using Microsoft.EntityFrameworkCore;

namespace LockDemo.Api.Data;

/// <summary>Fills an empty database with a plausible document review queue.</summary>
public static class SeedData
{
    private static readonly string[] Kinds =
        ["Bill of lading", "Proof of delivery", "Rate confirmation", "Fuel receipt", "Lumper receipt", "Detention invoice"];

    private static readonly string[] Carriers =
        ["Northwind Freight", "Blue Ridge Carriers", "Lakeside Logistics", "Summit Transport", "Harbor Line Trucking"];

    public static async Task EnsureSeededAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        if (await db.Records.AnyAsync(ct)) return;

        var now = clock.GetUtcNow();
        var rng = new Random(2026);
        for (var i = 1; i <= 60; i++)
        {
            db.Records.Add(new WorkRecord
            {
                Reference = $"DOC-2026-{i:0000}",
                Title = $"{Kinds[rng.Next(Kinds.Length)]} from {Carriers[rng.Next(Carriers.Length)]}",
                Status = i % 7 == 0 ? RecordStatus.InReview : RecordStatus.Pending,
                Priority = rng.Next(1, 4),
                UpdatedAt = now.AddMinutes(-rng.Next(5, 600)),
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
