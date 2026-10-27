using Microsoft.EntityFrameworkCore;

namespace RestaurantOS.Infrastructure.Persistence;

public static class Counters
{
    /// <summary>
    /// Atomically increments and returns a per-outlet sequence. Must run inside the caller's transaction so
    /// the increment commits or rolls back together with the document that uses it.
    /// </summary>
    public static async Task<long> NextAsync(this AppDbContext db, Guid outletId, string name, CancellationToken ct = default)
    {
        var values = await db.Database.SqlQuery<long>($"""
            INSERT INTO outlet_counters (outlet_id, name, value) VALUES ({outletId}, {name}, 1)
            ON CONFLICT (outlet_id, name) DO UPDATE SET value = outlet_counters.value + 1
            RETURNING value AS "Value"
            """).ToListAsync(ct);
        return values[0];
    }
}
