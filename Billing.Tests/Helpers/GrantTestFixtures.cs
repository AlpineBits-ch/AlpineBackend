using Billing.Domain.Aggregates;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;

namespace Billing.Tests.Helpers;

/// <summary>The tier sketch from monetization.md section 3.5, as configuration.</summary>
internal static class Plans
{
    public const string Free = "free";
    public const string Plus = "plus";
    public const string Pro = "pro";

    public static PlanCatalogue Catalogue() => PlanCatalogue.FromOptions(Options());

    /// <summary>The same plans as bindable configuration, for the tests that need to know which plan
    /// an instance treats as its default rather than only what the plans contain.</summary>
    public static EntitlementPlanOptions Options() => new()
    {
        DefaultGuildPlan = Free,
        Plans = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Free] = new()
            {
                ["voice.max_participants"] = "10",
                ["voice.video_ceiling"] = "720p30",
                ["guild.emoji_slots"] = "50",
                ["guild.vanity_url"] = "false",
            },
            [Plus] = new()
            {
                ["voice.max_participants"] = "25",
                ["voice.video_ceiling"] = "1080p30",
                ["guild.emoji_slots"] = "200",
                ["guild.vanity_url"] = "false",
            },
            [Pro] = new()
            {
                ["voice.max_participants"] = "50",
                ["voice.video_ceiling"] = "1080p60",
                ["guild.emoji_slots"] = "500",
                ["guild.vanity_url"] = "true",
            },
        },
    };
}

/// <summary>Grants held in memory, so the source can be exercised without a database.</summary>
internal sealed class StubGrantProvider(params EntitlementGrant[] grants) : IGrantProvider
{
    public List<EntitlementGrant> Grants { get; } = [.. grants];

    public Task<IReadOnlyList<EntitlementGrant>> ActiveGrantsAsync(
        EntitlementSubject subject, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntitlementGrant>>(Grants);
}

/// <summary>A paid subscription, as far as the resolver is concerned.</summary>
internal sealed class StubSubscriptionSource(PlanDefinition plan) : IEntitlementSource
{
    public EntitlementPrecedence Precedence => EntitlementPrecedence.Subscription;

    /// <summary>Every time it was asked, so a test can show a grant never displaced it.</summary>
    public int Calls { get; private set; }

    public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(plan.ToSet(EntitlementPrecedence.Subscription));
    }
}

internal static class Subjects
{
    public static readonly EntitlementSubject Guild = EntitlementSubject.ForGuild("gild_test_guild");
    public static readonly EntitlementSubject User = EntitlementSubject.ForUser("user_test_user");
}

/// <summary>A clock a test can move, so expiry can be observed rather than waited for.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void MoveTo(DateTimeOffset instant) => _now = instant;
}

internal static class GrantFixtures
{
    /// <summary>A plan grant on the shared test guild.</summary>
    public static Grant PlanGrant(
        string plan = Plans.Pro,
        DateTimeOffset? expiresAt = null,
        SubjectKind subjectKind = SubjectKind.Guild,
        string? subjectId = null) => new()
    {
        Id = Grant.GenerateId(),
        SubjectKind = subjectKind,
        SubjectId = subjectId ?? Subjects.Guild.Id,
        GrantKind = GrantKind.Plan,
        Plan = plan,
        ExpiresAt = expiresAt,
        Reason = "Compensation for the outage on the 3rd.",
        Source = GrantSource.Staff,
        CreatedBy = "user_staff",
    };

    public static EntitlementKey Participants => EntitlementKeys.VoiceMaxParticipants;

    public static EntitlementKey Emoji => EntitlementKeys.GuildEmojiSlots;

    public static EntitlementKey VanityUrl => EntitlementKeys.GuildVanityUrl;

    public static EntitlementKey VideoCeiling => EntitlementKeys.VoiceVideoCeiling;
}
