using System.Globalization;

namespace Echo.Realtime.LiveKit;

/// <summary>One SFU node, and the two addresses it answers on.</summary>
/// <param name="Region">The node's region tag, as it appears in the room registry.</param>
public sealed record LiveKitNode(string Region, string SignalingUrl, string ApiUrl);

/// <summary>Everything the backend needs to talk to the LiveKit fleet.</summary>
public sealed record LiveKitOptions
{
    /// <summary>The <c>iss</c> claim of every token this deployment mints.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The HS256 signing key.</summary>
    public string ApiSecret { get; init; } = string.Empty;

    public IReadOnlyList<LiveKitNode> Nodes { get; init; } = [];

    /// <summary>How long a join token is good for.</summary>
    public TimeSpan JoinTokenTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Admin tokens are minted per control-plane call and used immediately, so this can be
    /// shorter still.</summary>
    public TimeSpan AdminTokenTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Seconds a room survives with nobody in it before the SFU deletes it.</summary>
    public int EmptyTimeoutSeconds { get; init; } = 300;

    /// <summary>Whether the fleet is configured at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ApiSecret)
        && !ApiKey.StartsWith("REPLACE_ME", StringComparison.OrdinalIgnoreCase)
        && Nodes.Count > 0;

    /// <summary>The node hosting a given region, or null when nothing here serves it.</summary>
    public LiveKitNode? Node(string region) =>
        Nodes.FirstOrDefault(n => string.Equals(n.Region, region, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a room with no recorded region goes.</summary>
    public LiveKitNode? SoleNode => Nodes.Count == 1 ? Nodes[0] : null;

    public static LiveKitOptions FromEnvironment() => new()
    {
        ApiKey = Raw("LIVEKIT_API_KEY") ?? string.Empty,
        ApiSecret = Raw("LIVEKIT_API_SECRET") ?? string.Empty,
        Nodes = ReadNodes(),
        JoinTokenTtl = Seconds("LIVEKIT_JOIN_TOKEN_TTL_SECONDS", TimeSpan.FromMinutes(10)),
        AdminTokenTtl = Seconds("LIVEKIT_ADMIN_TOKEN_TTL_SECONDS", TimeSpan.FromMinutes(5)),
        EmptyTimeoutSeconds = Number("LIVEKIT_EMPTY_TIMEOUT_SECONDS", 300),
    };

    /// <summary>
    /// Reads <c>LIVEKIT__NODES__0__REGION</c> and friends until an index is missing.
    /// </summary>
    private static IReadOnlyList<LiveKitNode> ReadNodes()
    {
        var nodes = new List<LiveKitNode>();

        for (var i = 0; ; i++)
        {
            var region = Raw($"LIVEKIT__NODES__{i}__REGION");
            var signaling = Raw($"LIVEKIT__NODES__{i}__SIGNALINGURL");
            var api = Raw($"LIVEKIT__NODES__{i}__APIURL");

            if (region is null && signaling is null && api is null) break;
            if (region is null || signaling is null || api is null) continue;

            nodes.Add(new LiveKitNode(region, signaling.TrimEnd('/'), api.TrimEnd('/')));
        }

        return nodes;
    }

    private static string? Raw(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int Number(string name, int fallback) =>
        Raw(name) is { } raw
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : fallback;

    private static TimeSpan Seconds(string name, TimeSpan fallback) =>
        Raw(name) is { } raw
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? TimeSpan.FromSeconds(parsed)
            : fallback;
}

/// <summary>The one place that decides which region a room belongs to.</summary>
public static class LiveKitRegions
{
    /// <summary>The fleet's only region today.</summary>
    public static string Default =>
        Environment.GetEnvironmentVariable("LIVEKIT_DEFAULT_REGION") is { Length: > 0 } configured
            ? configured.Trim()
            : "fsn1";
}
