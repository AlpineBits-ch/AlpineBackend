using Echo.Domain.Enums;
using Persistence;

namespace Echo.Domain.Entities.Status;

/// <summary>A named piece of the platform, as a user would describe it.</summary>
public class StatusComponent : BaseEntity<StatusComponent>, IPrefixedEntity
{
    public static string Prefix => "stcp";

    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 200;
    public const int MaxImpactHintLength = 300;

    /// <summary>Stable machine name (<c>accounts</c>, <c>messages</c>).</summary>
    public string Key { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>
    /// The plain sentence a generated incident is built from: "Some people may not be able to sign
    /// in or create an account."
    /// </summary>
    public string ImpactHint { get; set; } = string.Empty;

    /// <summary>
    /// The YARP cluster ids whose responses and destination health decide this component's status.
    /// </summary>
    public List<string> Clusters { get; set; } = [];

    public int Position { get; set; }

    /// <summary>Hidden components still collect samples and still open incidents in the console; they
    /// just do not render publicly. Lets a new component be watched for a week before it is shown.</summary>
    public bool IsVisible { get; set; } = true;

    public ComponentStatus Status { get; set; } = ComponentStatus.Operational;

    /// <summary>When <see cref="Status"/> last changed.</summary>
    public DateTimeOffset StatusSince { get; set; }

    /// <summary>Error rate at or above which this component is degraded, as a fraction.</summary>
    public double? DegradedRate { get; set; }

    /// <summary>Error rate at or above which this component is a major outage, as a fraction.</summary>
    public double? OutageRate { get; set; }

    /// <summary>Requests needed in the window before an error rate is believed at all.</summary>
    public int? MinimumVolume { get; set; }

    public static StatusComponent Create(StatusComponentSeed seed, DateTimeOffset now) => new()
    {
        Id = GenerateId(),
        CreatedAt = now,
        UpdatedAt = now,
        Key = seed.Key,
        Name = seed.Name,
        Description = seed.Description,
        ImpactHint = seed.ImpactHint,
        Clusters = [.. seed.Clusters],
        Position = seed.Position,
        IsVisible = true,
        Status = ComponentStatus.Operational,
        StatusSince = now,
    };

    public void SetStatus(ComponentStatus status, DateTimeOffset now)
    {
        if (Status == status) return;

        Status = status;
        StatusSince = now;
        UpdatedAt = now;
    }
}

/// <summary>One row of the built-in catalog.</summary>
public sealed record StatusComponentSeed(
    string Key,
    string Name,
    string Description,
    string ImpactHint,
    IReadOnlyList<string> Clusters,
    int Position);

/// <summary>The components an instance starts with, and the clusters each watches.</summary>
public static class StatusComponentCatalog
{
    /// <summary>The gateway itself. No cluster: its signal is the gateway's own unhandled 5xx.</summary>
    public const string GatewayKey = "api";

    /// <summary>The websocket. No cluster: its signal is hub connection and invocation failures.</summary>
    public const string RealtimeKey = "realtime";

    public static IReadOnlyList<StatusComponentSeed> Seeds { get; } =
    [
        new("accounts", "Sign-in and accounts",
            "Signing in, registration, sessions",
            "Some people may not be able to sign in or create an account.",
            ["identity-cluster", "identity-connect-cluster", "identity-oauth-cluster"], 10),

        new("messages", "Direct messages",
            "Sending and reading direct messages",
            "Some messages may fail to send or may take longer than usual to arrive.",
            ["messaging-cluster"], 20),

        new("servers", "Servers and channels",
            "Server channels, members, roles and moderation",
            "Some people may have trouble opening servers, or posting in channels.",
            ["guild-cluster"], 30),

        // Voice shares Guild's cluster: the signalling that joins a call is a Guild route.
        new("voice", "Voice and video",
            "Voice channels and calls",
            "Some people may not be able to join a call, or may be disconnected from one.",
            ["guild-cluster"], 40),

        new("friends", "Friends and presence",
            "Friend requests, online status and activity",
            "Friend requests and online status may not update.",
            ["social-cluster"], 50),

        new("bots", "Bots and apps",
            "The bot API and slash commands",
            "Bots may be offline or may not respond to commands.",
            ["bots-cluster"], 60),

        new("previews", "Link previews",
            "Preview cards for links in messages",
            "Links in messages may appear without a preview card.",
            ["unfurl-cluster"], 70),

        new("imports", "Discord import",
            "Importing servers and history from Discord",
            "New imports may not start, and running imports may be paused.",
            ["imports-cluster"], 80),

        new("federation", "Federation",
            "Talking to other venta instances",
            "Messages to and from other instances may be delayed.",
            ["federation-cluster", "federation-document-cluster"], 90),

        new("isle", "The Isle integration",
            "Game server links, in-game commands and proximity voice",
            "In-game commands and server links may not work.",
            ["isle-cluster"], 100),

        new("discovery", "Community discovery",
            "Browsing public communities and topics",
            "Discover may not load, and searching for communities may fail.",
            ["discovery-cluster"], 105),

        new(RealtimeKey, "Realtime connection",
            "The live connection that delivers messages as they happen",
            "The app may not update on its own; messages may only appear after a refresh.",
            [], 110),

        new(GatewayKey, "API gateway",
            "The front door every app connects through",
            "Apps may not connect at all, or may fail on most actions.",
            [], 120),
    ];

    public static IReadOnlyCollection<string> AllClusters { get; } =
        Seeds.SelectMany(s => s.Clusters).Distinct().ToArray();
}
