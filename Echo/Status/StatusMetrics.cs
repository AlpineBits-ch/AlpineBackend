using System.Collections.Concurrent;
using Yarp.ReverseProxy.Model;

namespace Echo.Status;

/// <summary>One window's worth of counting for one cluster.</summary>
public readonly record struct ClusterSample(int Total, int Errors)
{
    public double ErrorRate => Total == 0 ? 0 : (double)Errors / Total;

    public static ClusterSample operator +(ClusterSample a, ClusterSample b) =>
        new(a.Total + b.Total, a.Errors + b.Errors);
}

/// <summary>
/// Counts responses per YARP cluster in a small rolling window, in memory, per replica.
/// </summary>
public sealed class StatusMetrics(StatusOptions options)
{
    /// <summary>Pseudo-cluster prefix for components with no backend of their own.</summary>
    public const string LocalPrefix = "~";

    public static string LocalKey(string componentKey) => LocalPrefix + componentKey;

    private readonly ConcurrentDictionary<string, Bucket[]> _clusters = new();
    private readonly int _bucketSeconds = Math.Max(1, (int)options.Interval.TotalSeconds);

    public void Record(string clusterId, int statusCode, bool aborted)
    {
        // 4xx is the system working: a wrong password, a message that is gone, a client over its
        // rate limit.
        if (aborted) return;

        Add(clusterId, isError: statusCode >= 500);
    }

    public void Add(string clusterId, bool isError)
    {
        var buckets = _clusters.GetOrAdd(clusterId, _ => CreateBuckets());
        var epoch = CurrentEpoch();
        var bucket = buckets[(int)(epoch % buckets.Length)];

        bucket.RollTo(epoch);

        Interlocked.Increment(ref bucket.Total);
        if (isError) Interlocked.Increment(ref bucket.Errors);
    }

    /// <summary>The current window for one cluster.</summary>
    public ClusterSample Read(string clusterId)
    {
        if (!_clusters.TryGetValue(clusterId, out var buckets)) return default;

        var newest = CurrentEpoch();
        var oldest = newest - (options.WindowBuckets - 1);
        var sample = default(ClusterSample);

        foreach (var bucket in buckets)
        {
            var epoch = Volatile.Read(ref bucket.Epoch);
            if (epoch < oldest || epoch > newest) continue;

            sample += new ClusterSample(Volatile.Read(ref bucket.Total), Volatile.Read(ref bucket.Errors));
        }

        return sample;
    }

    public ClusterSample Read(IEnumerable<string> clusterIds)
    {
        var sample = default(ClusterSample);
        foreach (var id in clusterIds) sample += Read(id);
        return sample;
    }

    private long CurrentEpoch() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / _bucketSeconds;

    private Bucket[] CreateBuckets()
    {
        // One spare bucket beyond the window so the one currently being written is never also the
        // one being aged out from under the reader.
        var buckets = new Bucket[options.WindowBuckets + 1];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = new Bucket();
        return buckets;
    }

    private sealed class Bucket
    {
        public long Epoch = -1;
        public int Total;
        public int Errors;

        /// <summary>Resets the bucket when it comes back around to a new epoch.</summary>
        public void RollTo(long epoch)
        {
            if (Volatile.Read(ref Epoch) == epoch) return;

            lock (this)
            {
                if (Epoch == epoch) return;

                Epoch = epoch;
                Total = 0;
                Errors = 0;
            }
        }
    }
}

public static class StatusMetricsMiddleware
{
    /// <summary>Counts every response on its way out.</summary>
    public static WebApplication UseStatusMetrics(this WebApplication app)
    {
        var metrics = app.Services.GetRequiredService<StatusMetrics>();

        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch
            {
                // An exception that escapes to here is a gateway 500 whether or not anything writes
                // that status onto the response, so it is counted before being rethrown.
                metrics.Add(ClusterOf(context) ?? StatusMetrics.LocalKey(GatewayComponentKey), isError: true);
                throw;
            }

            var aborted = context.RequestAborted.IsCancellationRequested;
            var cluster = ClusterOf(context);

            if (cluster is not null)
            {
                metrics.Record(cluster, context.Response.StatusCode, aborted);
                return;
            }

            // Gateway-local.
            if (context.Request.Path.StartsWithSegments("/api/v1/status")) return;

            metrics.Record(StatusMetrics.LocalKey(GatewayComponentKey), context.Response.StatusCode, aborted);
        });

        return app;
    }

    private const string GatewayComponentKey = "api";

    private static string? ClusterOf(HttpContext context) =>
        context.Features.Get<IReverseProxyFeature>()?.Route.Config.ClusterId;
}
