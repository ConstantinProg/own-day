using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Diagnostics;

internal static class DiagnosticFencing
{
    // The two-key namespace is separate from the single-key business locks.
    internal const int LockNamespace = 18763;
    internal const int DocumentLockNamespace = 18764;

    internal static async Task LockDocumentsAsync(OwnDayDbContext db, long owner, CancellationToken token)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DocumentLockNamespace}, {owner.GetHashCode()})", token);
    }

    internal static async Task AdvanceAsync(OwnDayDbContext db, long owner, bool clearHistory, CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) return;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockNamespace}, {owner.GetHashCode()})", token);
        var generation = Guid.NewGuid();
        if (clearHistory)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO diagnostic_fences ("OwnerId", "Generation", "DetailGeneration") VALUES ({owner}, {generation}, {generation})
                ON CONFLICT ("OwnerId") DO UPDATE SET "Generation" = EXCLUDED."Generation", "DetailGeneration" = EXCLUDED."DetailGeneration"
                """, token);
            await db.Set<DiagnosticEvent>().Where(row => row.OwnerId == owner).ExecuteDeleteAsync(token);
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO diagnostic_fences ("OwnerId", "Generation", "DetailGeneration") VALUES ({owner}, {Guid.Empty}, {generation})
                ON CONFLICT ("OwnerId") DO UPDATE SET "DetailGeneration" = EXCLUDED."DetailGeneration"
                """, token);
        }
    }
}
