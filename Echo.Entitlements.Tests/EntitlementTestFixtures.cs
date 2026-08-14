using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Echo.Entitlements.Tests;

/// <summary>A source that says exactly what a test told it to say, at the precedence the test
/// chose. Sources in production talk to Billing; here the point is the merge, not the fetch.
/// </summary>
internal sealed class StubSource(
    EntitlementPrecedence precedence,
    Func<EntitlementSubject, EntitlementSet> answer,
    bool shortCircuits = false) : IEntitlementSource
{
    public EntitlementPrecedence Precedence { get; } = precedence;

    public bool ShortCircuits { get; } = shortCircuits;

    /// <summary>Every subject this source was asked about, so a test can assert that a
    /// short-circuiting source above it meant it was never consulted at all.</summary>
    public List<EntitlementSubject> Asked { get; } = [];

    public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken)
    {
        Asked.Add(subject);
        return Task.FromResult(answer(subject));
    }

    public static StubSource Returning(
        EntitlementPrecedence precedence,
        Action<EntitlementSetBuilder> build,
        bool shortCircuits = false)
    {
        var builder = new EntitlementSetBuilder(precedence);
        build(builder);
        var set = builder.Build();
        return new StubSource(precedence, _ => set, shortCircuits);
    }

    /// <summary>Answers only for one subject kind, which is how a real guild-plan source and a real
    /// Venta Plus source behave.</summary>
    public static StubSource ForKind(
        EntitlementPrecedence precedence, SubjectKind kind, Action<EntitlementSetBuilder> build)
    {
        var builder = new EntitlementSetBuilder(precedence);
        build(builder);
        var set = builder.Build();
        return new StubSource(precedence, subject => subject.Kind == kind ? set : EntitlementSet.Empty);
    }
}

/// <summary>The tier sketch from monetization.md section 3.5, as configuration.</summary>
internal static class TierFixtures
{
    public const string FreeGuild = "free";
    public const string PlusGuild = "plus";
    public const string ProGuild = "pro";
    public const string FreeUser = "user_free";
    public const string VentaPlus = "venta_plus";

    public static EntitlementPlanOptions Options() => new()
    {
        DefaultGuildPlan = FreeGuild,
        DefaultUserPlan = FreeUser,
        Plans = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [FreeGuild] = new()
            {
                ["voice.max_participants"] = "10",
                ["voice.video_ceiling"] = "720p30",
                ["voice.max_publishers"] = "2",
                ["storage.upload_max_bytes"] = "26214400",
                ["storage.guild_quota_bytes"] = "5368709120",
                ["guild.emoji_slots"] = "50",
                ["guild.bots_installed"] = "3",
                ["guild.vanity_url"] = "false",
                ["guild.audit_log_days"] = "30",
            },
            [PlusGuild] = new()
            {
                ["voice.max_participants"] = "25",
                ["voice.video_ceiling"] = "1080p30",
                ["voice.max_publishers"] = "4",
                ["storage.upload_max_bytes"] = "104857600",
                ["storage.guild_quota_bytes"] = "107374182400",
                ["guild.emoji_slots"] = "200",
                ["guild.bots_installed"] = "10",
                ["guild.vanity_url"] = "false",
                ["guild.audit_log_days"] = "90",
            },
            [ProGuild] = new()
            {
                ["voice.max_participants"] = "50",
                ["voice.video_ceiling"] = "1080p60",
                ["voice.max_publishers"] = "8",
                ["storage.upload_max_bytes"] = "524288000",
                ["storage.guild_quota_bytes"] = "1099511627776",
                ["guild.emoji_slots"] = "500",
                ["guild.bots_installed"] = "unlimited",
                ["guild.vanity_url"] = "true",
                ["guild.audit_log_days"] = "365",
            },
            [FreeUser] = new()
            {
                ["voice.video_ceiling"] = "720p30",
                ["storage.upload_max_bytes"] = "26214400",
                ["user.upload_max_bytes"] = "26214400",
                ["user.max_devices"] = "5",
            },
            [VentaPlus] = new()
            {
                ["voice.video_ceiling"] = "1080p60",
                ["storage.upload_max_bytes"] = "524288000",
                ["user.upload_max_bytes"] = "524288000",
                ["user.max_devices"] = "10",
            },
        },
    };

    public static PlanCatalogue Catalogue() => PlanCatalogue.FromOptions(Options());
}

/// <summary>Puts a named plan on each side, standing in for the Billing lookup that does not exist
/// yet.</summary>
internal sealed class ScriptedPlanAssignment(string? guildPlan, string? userPlan) : IPlanAssignment
{
    public ValueTask<string?> PlanNameForAsync(
        EntitlementSubject subject, CancellationToken cancellationToken) =>
        ValueTask.FromResult(subject.Kind == SubjectKind.Guild ? guildPlan : userPlan);
}

internal static class Subjects
{
    public static readonly EntitlementSubject Guild = EntitlementSubject.ForGuild("guild-1");
    public static readonly EntitlementSubject User = EntitlementSubject.ForUser("user-1");
}
