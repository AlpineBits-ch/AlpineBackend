using Echo.Domain.Entities.Status;
using Echo.Persistence.Persistance;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy;

namespace Echo.Status;

/// <summary>
/// Puts the built-in component catalog in the database once, and complains about drift.
/// </summary>
public static class StatusSeeder
{
    /// <summary>
    /// Inserts any catalog entry whose key has no row yet, and touches nothing else.
    /// </summary>
    public static async Task<int> EnsureAsync(MicroserviceContext db, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.StatusComponents.Select(c => c.Key).ToListAsync(ct);
        var known = existing.ToHashSet(StringComparer.Ordinal);

        var added = 0;

        foreach (var seed in StatusComponentCatalog.Seeds)
        {
            if (known.Contains(seed.Key)) continue;

            db.StatusComponents.Add(StatusComponent.Create(seed, now));
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);

        return added;
    }

    /// <summary>
    /// Logs both directions of drift between the seeded components and YARP's actual clusters.
    /// </summary>
    public static void WarnAboutDrift(IReadOnlyCollection<string> proxyClusters, ILogger logger)
    {
        var monitored = StatusComponentCatalog.AllClusters.ToHashSet(StringComparer.Ordinal);

        var unknown = monitored.Except(proxyClusters, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            logger.LogWarning(
                "Status components reference {Count} cluster(s) the proxy does not define: {Clusters}. "
                + "Those components will never see a request and will never report an outage.",
                unknown.Length, string.Join(", ", unknown));
        }

        var unwatched = proxyClusters.Except(monitored, StringComparer.Ordinal).ToArray();
        if (unwatched.Length > 0)
        {
            logger.LogWarning(
                "{Count} proxy cluster(s) are watched by no status component: {Clusters}. "
                + "An outage of those services will not appear on the status page.",
                unwatched.Length, string.Join(", ", unwatched));
        }
    }

    public static IReadOnlyCollection<string> ClusterIds(IProxyStateLookup lookup) =>
        lookup.GetClusters().Select(c => c.ClusterId).ToArray();
}
