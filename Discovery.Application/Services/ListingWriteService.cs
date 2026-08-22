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
}

/// <summary>What every <see cref="ListingWriteService"/> call answers: the listing where one exists
/// (even on refusal, so an endpoint can read <c>BumpAvailableAt</c> off a cooldown refusal), the
/// built DTO on success, and why not otherwise.</summary>
public sealed record ListingWriteResult(Listing? Listing, ListingDto? Dto, ListingWriteRefusal Refusal, string? Message = null)
{
    public bool Success => Refusal == ListingWriteRefusal.None;

    public static ListingWriteResult Ok(Listing listing, ListingDto dto) => new(listing, dto, ListingWriteRefusal.None);
    public static ListingWriteResult Invalid(string message) => new(null, null, ListingWriteRefusal.Invalid, message);
    public static ListingWriteResult NotFound() => new(null, null, ListingWriteRefusal.NotFound);
    public static ListingWriteResult NotEntitled(Listing listing) => new(listing, null, ListingWriteRefusal.NotEntitled);
    public static ListingWriteResult CooldownActive(Listing listing) => new(listing, null, ListingWriteRefusal.CooldownActive);
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
    EntitlementResolver? entitlements = null)
{
    private const int HeadlineMaxLength = 80;
    private const int PitchMaxLength = 600;
    private const int MinTopics = 1;
    private const int MaxTopics = 8;
    private const int MaxLinks = 3;

    /// <summary>
    /// Where a listing may point people off-platform. Not read from configuration: every allowed
    /// host is a well-known community or social platform, not an operator-tunable setting, and
    /// nothing in this repo yet has a per-service config surface to hang it on.
    /// </summary>
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
        var topics = new List<TopicInput>();
        foreach (var raw in dto.Topics)
        {
            if (!TopicRef.TryParse(raw, out var topic)) return ListingWriteResult.Invalid($"Not a topic: {raw}");

            // TopicRef.TryParse slugs the id and does not hand the pre-slug text back - recompute
            // the same substring so a minted tag gets a readable display name, not its slug.
            var separator = raw.IndexOf(':');
            topics.Add(new TopicInput(topic, separator >= 0 ? raw[(separator + 1)..] : raw));
        }

        var distinctTopics = topics.GroupBy(t => t.Topic).Select(g => g.First()).ToList();

        if (!Enum.TryParse<JoinPolicy>(dto.JoinPolicy, ignoreCase: true, out var joinPolicy))
            return ListingWriteResult.Invalid("Join policy must be Open or Application.");

        if (Validate(dto, distinctTopics) is { } problem) return ListingWriteResult.Invalid(problem);

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

        await resolver.EnsureTagsAsync(distinctTopics, ct);

        var requested = distinctTopics.Select(t => t.Topic).ToHashSet();
        var alreadyPresent = listing.Topics.Select(t => new TopicRef(t.Kind, t.TopicId)).ToHashSet();

        foreach (var stale in listing.Topics.Where(row => !requested.Contains(new TopicRef(row.Kind, row.TopicId))).ToList())
            ctx.ListingTopics.Remove(stale);

        foreach (var input in distinctTopics)
        {
            if (alreadyPresent.Contains(input.Topic)) continue;
            ctx.ListingTopics.Add(ListingTopic.For(listing.Id, input.Topic));
        }

        return ListingWriteResult.Ok(listing, await DescribeAsync(listing, requested, ct));
    }

    /// <summary>
    /// Publishes the guild's draft, gated on the entitlement. Keeps the original
    /// <c>PublishedAt</c> on a republish - see <see cref="Listing.Publish"/>.
    /// </summary>
    public async Task<ListingWriteResult> PublishAsync(string guildId, CancellationToken ct)
    {
        var listing = await LoadAsync(guildId, ct);
        if (listing is null) return ListingWriteResult.NotFound();

        if (!await IsEntitledAsync(guildId, ct)) return ListingWriteResult.NotEntitled(listing);

        listing.Publish(clock.GetUtcNow());
        return await SuccessAsync(listing, ct);
    }

    /// <summary>
    /// Owner-initiated withdrawal. No entitlement check: giving a listing up must stay reachable on
    /// every plan, the same as <c>VanityUrlService</c> lets a downgraded guild clear its slug.
    /// </summary>
    public async Task<ListingWriteResult> UnlistAsync(string guildId, CancellationToken ct)
    {
        var listing = await LoadAsync(guildId, ct);
        if (listing is null) return ListingWriteResult.NotFound();

        listing.Unlist();
        return await SuccessAsync(listing, ct);
    }

    /// <summary>Refreshes <c>LastBumpedAt</c> for ranking, once per cooldown. A listing that is not
    /// published cannot bump - <see cref="Listing.Bump"/> - which already covers a suspended or
    /// lapsed guild without a second entitlement check here.</summary>
    public async Task<ListingWriteResult> BumpAsync(string guildId, CancellationToken ct)
    {
        var listing = await LoadAsync(guildId, ct);
        if (listing is null) return ListingWriteResult.NotFound();

        if (!listing.Bump(clock.GetUtcNow())) return ListingWriteResult.CooldownActive(listing);

        return await SuccessAsync(listing, ct);
    }

    /// <summary>Builds the wire DTO for a listing whose <see cref="Listing.Topics"/> is already the
    /// committed, correct set - used by the endpoint's read route and by every write here except
    /// the draft save, which resolves against the requested set instead since <c>Topics</c> reflects
    /// entities that may not exist in the store yet.</summary>
    public Task<ListingDto> DescribeAsync(Listing listing, CancellationToken ct) =>
        DescribeAsync(listing, listing.Topics.Select(t => new TopicRef(t.Kind, t.TopicId)).ToHashSet(), ct);

    /// <summary>
    /// The entitlement read behind <see cref="PublishAsync"/> only. Strict: unlike the display path
    /// (<c>VanityUrlService.IsEntitledAsync(strict: false)</c>), a published listing is a persistent
    /// public artifact, so an unreadable Billing service must fail closed rather than let one out.
    /// No resolver at all is a host with no billing wired up - self-hosted, or a test - which every
    /// source in this repo treats as "everything included".
    /// </summary>
    private async Task<bool> IsEntitledAsync(string guildId, CancellationToken ct)
    {
        if (entitlements is null) return true;

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

    private async Task<ListingWriteResult> SuccessAsync(Listing listing, CancellationToken ct) =>
        ListingWriteResult.Ok(listing, await DescribeAsync(listing, ct));

    /// <summary>
    /// Resolves <paramref name="topicRefs"/> into <see cref="TopicDto"/>s. Falls back to a tag this
    /// same call just minted via <c>EnsureTagsAsync</c> but has not saved yet - it will not come back
    /// from <c>resolver.ResolveAsync</c>, which queries the store, the same trap
    /// <c>InterestService.DescribeAsync</c> works around.
    /// </summary>
    private async Task<ListingDto> DescribeAsync(Listing listing, IReadOnlySet<TopicRef> topicRefs, CancellationToken ct)
    {
        var resolved = await resolver.ResolveAsync(topicRefs, ct);
        var byRef = resolved.ToDictionary(t => new TopicRef(t.Kind == "game" ? TopicKind.Game : TopicKind.Tag, t.Id));

        var minted = ctx.ChangeTracker.Entries<Tag>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToDictionary(t => new TopicRef(TopicKind.Tag, t.Slug));

        var topics = topicRefs.Select(r => byRef.TryGetValue(r, out var dto)
                ? dto
                : new TopicDto { Kind = "tag", Id = r.Id, Name = minted.TryGetValue(r, out var tag) ? tag.DisplayName : r.Id })
            .ToList();

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
            PublishedAt = listing.PublishedAt,
            LastBumpedAt = listing.LastBumpedAt,
            BumpAvailableAt = listing.BumpAvailableAt,
        };
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
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !AllowedLinkHosts.Contains(uri.Host))
                return $"Link is not on an allowed host: {link}";
        }

        if (!Bcp47.IsMatch(dto.Language)) return "Language must be a well-formed BCP-47 tag.";

        return null;
    }
}
