using System.Text.RegularExpressions;
using Discovery.Api.Dtos.Request;
using Discovery.Api.Dtos.Response;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Discovery.Infrastructure.Persistence;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Services;

/// <summary>Why a write did not happen. <see cref="None"/> means it did.</summary>
public enum ListingWriteRefusal
{
    None,
    Invalid,
    NotFound,
    NotEntitled,
    CooldownActive,
    NotPublished,
    Banned,
}

/// <summary>What every <see cref="ListingWriteService"/> call answers: the listing where one exists
/// (even on refusal, so an endpoint can read <c>BumpAvailableAt</c> off a cooldown refusal), the
/// built DTO on success, and why not otherwise. <see cref="Changed"/> is only meaningful on success -
/// false means the call reached a domain no-op (see <see cref="ListingWriteService.UnlistAsync"/>),
/// so a caller that fans a change out over the network knows not to.</summary>
public sealed record ListingWriteResult(Listing? Listing, ListingDto? Dto, ListingWriteRefusal Refusal, string? Message = null, bool Changed = true)
{
    public static ListingWriteResult Ok(Listing listing, ListingDto dto, bool changed = true) =>
        new(listing, dto, ListingWriteRefusal.None, Changed: changed);
    public static ListingWriteResult Invalid(string message) => new(null, null, ListingWriteRefusal.Invalid, message);
    public static ListingWriteResult NotFound() => new(null, null, ListingWriteRefusal.NotFound);
    public static ListingWriteResult NotEntitled(Listing listing) => new(listing, null, ListingWriteRefusal.NotEntitled);
    public static ListingWriteResult CooldownActive(Listing listing) => new(listing, null, ListingWriteRefusal.CooldownActive);
    public static ListingWriteResult NotPublished(Listing listing) => new(listing, null, ListingWriteRefusal.NotPublished);
    public static ListingWriteResult Banned(Listing listing, string reason) => new(listing, null, ListingWriteRefusal.Banned, reason);
}

/// <summary>
/// Draft, publish, unlist and bump a guild's one listing. The plan gate lives only in
/// <see cref="PublishAsync"/> - see the class remarks on <see cref="IsEntitledAsync"/> for why the
/// draft write must never call it.
/// </summary>
public class ListingWriteService(
    MicroserviceContext ctx,
    TopicResolver resolver,
    TimeProvider clock,
    ILogger<ListingWriteService> logger,
    EntitlementResolver entitlements,
    DiscoveryBanService bans)
{
    private const int HeadlineMaxLength = 80;
    private const int PitchMaxLength = 600;
    private const int MinTopics = 1;
    private const int MaxTopics = 8;
    private const int MaxLinks = 3;

    // Not yet configurable - no per-service config surface exists for this. Lift it out into one
    // the day an operator needs to tune it.
    private static readonly HashSet<string> AllowedLinkHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.gg", "discord.com",
        "twitter.com", "x.com",
        "youtube.com", "youtu.be",
        "twitch.tv",
        "instagram.com",
        "tiktok.com",
        "reddit.com",
        "steamcommunity.com",
        "patreon.com",
        "roll20.net",
        "dndbeyond.com",
        "startplaying.games",
        "worldanvil.com",
        "bsky.app",
    };

    // Well-formed, not valid: RFC 5646 draws that line deliberately, and checking against the
    // subtag registry would reject any language it has not been updated to know about.
    private static readonly Regex Bcp47 = new(@"^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.Compiled);

    /// <summary>
    /// Creates the guild's listing on first use, or overwrites the existing one. Never checks the
    /// entitlement - this is the autosave path, called far more often than a person makes a
    /// deliberate decision, and the plan check belongs on the action that actually goes public.
    /// </summary>
    public async Task<ListingWriteResult> UpsertDraftAsync(string guildId, UpsertListingDraftDto dto, CancellationToken ct)
    {
        if (!TopicInput.TryParseAll(dto.Topics, out var topics, out var badRef))
            return ListingWriteResult.Invalid($"Not a topic: {badRef}");

        var distinctTopics = topics.GroupBy(t => t.Topic).Select(g => g.First()).ToList();

        if (!Enum.TryParse<JoinPolicy>(dto.JoinPolicy, ignoreCase: true, out var joinPolicy))
            return ListingWriteResult.Invalid("Join policy must be Open or Application.");

        if (Validate(dto, distinctTopics) is { } problem) return ListingWriteResult.Invalid(problem);

        // Games are never minted - unlike a tag, an unknown game id is a bad request, not a new row.
        // Checked before EnsureTagsAsync touches the context, so a request naming one game that does
        // not exist mints no tags either. Mirrors InterestService.ReplaceAsync.
        var gameIds = distinctTopics.Where(t => t.Topic.Kind == TopicKind.Game).Select(t => t.Topic.Id).ToList();
        if (gameIds.Count > 0)
        {
            var knownGameIds = await ctx.GameTopics
                .Where(g => gameIds.Contains(g.GameApplicationId))
                .Select(g => g.GameApplicationId)
                .ToListAsync(ct);
            var unknown = gameIds.Except(knownGameIds).ToList();
            if (unknown.Count > 0) return ListingWriteResult.Invalid($"Unknown topic: game:{unknown[0]}");
        }

        var listing = await ctx.Listings.Include(l => l.Topics).FirstOrDefaultAsync(l => l.GuildId == guildId, ct);
        if (listing is null)
        {
            listing = Listing.Create(guildId);
            ctx.Listings.Add(listing);
        }

        listing.Headline = dto.Headline.Trim();
        listing.Pitch = dto.Pitch.Trim();
        listing.Language = dto.Language.Trim();
        listing.JoinPolicy = joinPolicy;
        listing.Links = dto.Links.ToList();

        var minted = await resolver.EnsureTagsAsync(distinctTopics, ct);

        var requested = distinctTopics.Select(t => t.Topic).ToHashSet();
        var alreadyPresent = listing.Topics.Select(t => new TopicRef(t.Kind, t.TopicId)).ToHashSet();

        foreach (var stale in listing.Topics.Where(row => !requested.Contains(new TopicRef(row.Kind, row.TopicId))).ToList())
            ctx.ListingTopics.Remove(stale);

        foreach (var input in distinctTopics)
        {
            if (alreadyPresent.Contains(input.Topic)) continue;
            ctx.ListingTopics.Add(ListingTopic.For(listing.Id, input.Topic));
        }

        return ListingWriteResult.Ok(listing, await DescribeAsync(listing, requested, minted, ct));
    }

    /// <summary>
    /// Publishes the guild's draft, gated on the entitlement. Keeps the original
    /// <c>PublishedAt</c> on a republish - see <see cref="Listing.Publish"/>.
    /// </summary>
    public async Task<ListingWriteResult> PublishAsync(string guildId, CancellationToken ct)
    {
        var listing = await LoadAsync(guildId, ct);
        if (listing is null) return ListingWriteResult.NotFound();

        var now = clock.GetUtcNow();

        // Checked before the entitlement: telling a banned guild to upgrade its plan is worse than
        // useless.
        if (await bans.IsBannedAsync(guildId, now, ct) is { } activeBan)
            return ListingWriteResult.Banned(listing, activeBan.Reason);

        if (!await IsEntitledAsync(guildId, ct)) return ListingWriteResult.NotEntitled(listing);

        listing.Publish(now);
        return await SuccessAsync(listing, ct);
    }

    /// <summary>
    /// Owner-initiated withdrawal. No entitlement check: giving a listing up must stay reachable on
    /// every plan, the same as <c>VanityUrlService</c> lets a downgraded guild clear its slug.
    /// <see cref="Listing.Unlist"/> no-ops on anything but a <c>Published</c> listing - the result
    /// carries that as <see cref="ListingWriteResult.Changed"/> so the endpoint does not fan out a
    /// state change that did not happen.
    /// </summary>
    public async Task<ListingWriteResult> UnlistAsync(string guildId, CancellationToken ct)
    {
        var listing = await LoadAsync(guildId, ct);
        if (listing is null) return ListingWriteResult.NotFound();

        var wasPublished = listing.State == ListingState.Published;
        listing.Unlist();
        return await SuccessAsync(listing, ct, changed: wasPublished);
    }

    /// <summary>
    /// Refreshes <c>LastBumpedAt</c> for ranking, once per cooldown. Checked here rather than left to
    /// <see cref="Listing.Bump"/>'s single false: that method answers false both for "not published"
    /// and "still cooling down", and collapsing them would tell a Draft or a plan-lapsed Suspended
    /// listing to wait out a cooldown that does not exist - the countdown the client renders would be
    /// counting down from nothing.
    /// </summary>
    public async Task<ListingWriteResult> BumpAsync(string guildId, CancellationToken ct)
    {
        var listing = await LoadAsync(guildId, ct);
        if (listing is null) return ListingWriteResult.NotFound();

        if (listing.State != ListingState.Published) return ListingWriteResult.NotPublished(listing);
        if (!listing.Bump(clock.GetUtcNow())) return ListingWriteResult.CooldownActive(listing);

        return await SuccessAsync(listing, ct);
    }

    /// <summary>Builds the wire DTO for a listing whose <see cref="Listing.Topics"/> is already the
    /// committed, correct set - used by the endpoint's read route and by every write here except
    /// the draft save, which resolves against the requested set instead since <c>Topics</c> reflects
    /// entities that may not exist in the store yet.</summary>
    public Task<ListingDto> DescribeAsync(Listing listing, CancellationToken ct) =>
        DescribeAsync(listing, listing.Topics.Select(t => new TopicRef(t.Kind, t.TopicId)).ToHashSet(), [], ct);

    /// <summary>
    /// The entitlement read behind <see cref="PublishAsync"/> only. Strict: unlike the display path
    /// (<c>VanityUrlService.IsEntitledAsync(strict: false)</c>), a published listing is a persistent
    /// public artifact, so an unreadable Billing service must fail closed rather than let one out.
    /// </summary>
    private async Task<bool> IsEntitledAsync(string guildId, CancellationToken ct)
    {
        try
        {
            var set = await entitlements.ResolveAsync(EntitlementSubject.ForGuild(guildId), ct);
            return set.Flag(EntitlementKeys.GuildPublicListing);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the public listing entitlement for {GuildId}", guildId);
            return false;
        }
    }

    private Task<Listing?> LoadAsync(string guildId, CancellationToken ct) =>
        ctx.Listings.Include(l => l.Topics).FirstOrDefaultAsync(l => l.GuildId == guildId, ct);

    private async Task<ListingWriteResult> SuccessAsync(Listing listing, CancellationToken ct, bool changed = true) =>
        ListingWriteResult.Ok(listing, await DescribeAsync(listing, ct), changed);

    /// <summary>
    /// Resolves <paramref name="topicRefs"/> into <see cref="TopicDto"/>s. Falls back to
    /// <paramref name="mintedTags"/> - <c>EnsureTagsAsync</c>'s own return value - for anything this
    /// same call just minted but has not saved yet, since it will not come back from
    /// <c>resolver.ResolveAsync</c>, which queries the store. Reading the tags back off the
    /// ChangeTracker instead would be a second path for turning a topic into a row, which spec
    /// section 16 rules out - see <c>InterestService.DescribeAsync</c>, the same fix applied there.
    /// </summary>
    private async Task<ListingDto> DescribeAsync(
        Listing listing, IReadOnlySet<TopicRef> topicRefs, IReadOnlyList<TopicDto> mintedTags, CancellationToken ct)
    {
        var resolved = await resolver.ResolveAsync(topicRefs, ct);
        var byRef = resolved.ToDictionary(t => new TopicRef(t.Kind == "game" ? TopicKind.Game : TopicKind.Tag, t.Id));
        var mintedByRef = mintedTags.ToDictionary(t => new TopicRef(TopicKind.Tag, t.Id));

        var topics = topicRefs.Select(r => byRef.TryGetValue(r, out var dto) ? dto : mintedByRef[r]).ToList();

        return new ListingDto
        {
            Id = listing.Id,
            GuildId = listing.GuildId,
            Headline = listing.Headline,
            Pitch = listing.Pitch,
            Language = listing.Language,
            JoinPolicy = listing.JoinPolicy.ToString(),
            Links = listing.Links,
            Topics = topics,
            State = listing.State.ToString(),
            SuspendedMessage = await SuspendedMessageAsync(listing, ct),
            PublishedAt = listing.PublishedAt,
            LastBumpedAt = listing.LastBumpedAt,
            BumpAvailableAt = listing.BumpAvailableAt,
        };
    }

    /// <summary>The owner-facing reason behind a staff suspension - the most recent ban row for the
    /// guild, even a lifted one: lifting a ban does not republish, so the listing can still be
    /// sitting Suspended/StaffAction with no active ban left to read the reason off.</summary>
    private async Task<string?> SuspendedMessageAsync(Listing listing, CancellationToken ct)
    {
        if (listing.State != ListingState.Suspended || listing.SuspendedReason != SuspensionReason.StaffAction)
            return null;

        var history = await bans.ListAsync(listing.GuildId, includeLifted: true, clock.GetUtcNow(), ct);
        return history.FirstOrDefault()?.Reason;
    }

    /// <summary>Rejects the whole request before anything is written - a partial write on a
    /// rejected request is worse than a clean refusal.</summary>
    private static string? Validate(UpsertListingDraftDto dto, IReadOnlyCollection<TopicInput> topics)
    {
        if (dto.Headline.Length > HeadlineMaxLength) return $"Headline must be at most {HeadlineMaxLength} characters.";
        if (dto.Pitch.Length > PitchMaxLength) return $"Pitch must be at most {PitchMaxLength} characters.";
        if (topics.Count is < MinTopics or > MaxTopics) return $"Between {MinTopics} and {MaxTopics} topics are required.";
        if (dto.Links.Count > MaxLinks) return $"At most {MaxLinks} links are allowed.";

        foreach (var link in dto.Links)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !AllowedLinkHosts.Contains(WithoutWww(uri.Host)))
            {
                return $"Only links to a known set of sites can be added right now, and {link} is not one of them.";
            }
        }

        if (!Bcp47.IsMatch(dto.Language)) return "Language must be a well-formed BCP-47 tag.";

        return null;
    }

    // Every site on the allowlist hands out both forms, and "www.youtube.com" is what copy-paste
    // actually gives a user - refusing it would make the allowlist reject its own entries.
    private static string WithoutWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
