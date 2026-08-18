using System.Text.Json;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Services;

/// <summary>
/// One persona the caller may speak as in one guild, flattened to exactly what the send path needs
/// so that resolving a message never re-reads the entity graph.
/// </summary>
public sealed class UsablePersona
{
    public string PersonaId { get; set; } = null!;
    public PersonaScope Scope { get; set; }
    public string? OwnerUserId { get; set; }

    /// <summary>The per-guild display name, falling back to the persona's own.</summary>
    public string Name { get; set; } = null!;

    public string? AvatarUrl { get; set; }
    public string? Tag { get; set; }
    public string? ProxyPrefix { get; set; }
    public string? ProxySuffix { get; set; }
    public bool IsRetired { get; set; }
    public PersonaApprovalState ApprovalState { get; set; }

    /// <summary>Whether this persona has ever been resolved onto a message, which is what makes a
    /// delete become a retire.</summary>
    public bool HasSpoken { get; set; }
}

/// <summary>What the send path should do with a message.</summary>
public enum PersonaResolutionOutcome
{
    /// <summary>Send as the user, with <see cref="PersonaResolution.Content"/> possibly unescaped.</summary>
    None,

    /// <summary>Send with the display overrides filled in and AuthorId untouched.</summary>
    Resolved,

    /// <summary>Refuse the send with a 403.</summary>
    Forbidden,
}

/// <summary>The outcome of resolving a persona for one message.</summary>
/// <param name="StickyPersonaId">Set when this send moved a Sticky channel's latched character,
/// which nobody asked for and which a second device otherwise never hears about.</param>
public sealed record PersonaResolution(
    PersonaResolutionOutcome Outcome,
    string Content,
    string? PersonaId = null,
    string? DisplayName = null,
    string? AvatarUrl = null,
    string? Error = null,
    string? StickyPersonaId = null);

/// <summary>Everything the resolver needs about one send.</summary>
public sealed class PersonaSendContext
{
    public required string UserId { get; init; }
    public required string GuildId { get; init; }
    public required string ChannelId { get; init; }

    /// <summary>The persona the client named explicitly, which wins over everything else.</summary>
    public string? PersonaId { get; init; }

    /// <summary>The plaintext body, or null for an encrypted send where no prefix can be read.</summary>
    public string? Content { get; init; }
}

/// <summary>The other persona a proxy prefix would also have selected.</summary>
public sealed record PersonaCollision(string PersonaId, string Name);

/// <summary>
/// Persona resolution and the caches around it. The usable-persona set is cached per (user, guild)
/// the way <see cref="GuildPermissionService"/> caches permissions, because this runs on every
/// single message send.
/// </summary>
public class PersonaService(IDistributedCache cache, MicroserviceContext ctx)
{
    private const string Version = "v1";

    private static string Encode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Keys the per-user sets of one guild behind a single token, so revoking a grant invalidates
    /// every member's set with one write rather than a key scan Redis cannot do cheaply.
    /// </summary>
    private static string EpochKey(string guildId) => $"guild:{Version}:{Encode(guildId)}:personaepoch";

    private static string SetKey(string guildId, string userId, string epoch) =>
        $"guild:{Version}:{Encode(guildId)}:user:{Encode(userId)}:personas:{epoch}";

    private async Task<string> GetEpochAsync(string guildId) =>
        await cache.GetStringAsync(EpochKey(guildId)) ?? "0";

    /// <summary>
    /// Drops every cached persona set in this guild. Called from every write that can change who
    /// may speak as what: personas, profiles and grants.
    /// </summary>
    public async Task InvalidateGuildAsync(string guildId) =>
        await cache.SetStringAsync(EpochKey(guildId), Guid.NewGuid().ToString("N"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });

    /// <summary>
    /// Drops the cached sets of every guild a persona is adopted into, for edits made outside any
    /// one guild - renaming an account-level persona changes what it renders as everywhere.
    /// </summary>
    public async Task InvalidatePersonaAsync(string personaId)
    {
        var guildIds = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.PersonaId == personaId)
            .Select(p => p.GuildId)
            .Distinct()
            .ToListAsync();

        foreach (var guildId in guildIds) await InvalidateGuildAsync(guildId);
    }

    /// <summary>
    /// Everything <paramref name="userId"/> may select in <paramref name="guildId"/>: their own
    /// adopted personas plus the guild-scoped ones they hold a grant on.
    /// </summary>
    public async Task<IReadOnlyList<UsablePersona>> GetUsablePersonasAsync(string userId, string guildId)
    {
        var key = SetKey(guildId, userId, await GetEpochAsync(guildId));

        var cached = await cache.GetStringAsync(key);
        if (!string.IsNullOrWhiteSpace(cached))
            return JsonSerializer.Deserialize<List<UsablePersona>>(cached) ?? [];

        var computed = await ComputeUsablePersonasAsync(userId, guildId);

        await cache.SetStringAsync(key, JsonSerializer.Serialize(computed), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
        });

        return computed;
    }

    private async Task<List<UsablePersona>> ComputeUsablePersonasAsync(string userId, string guildId)
    {
        var memberId = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId && m.UserId == userId)
            .Select(m => m.Id)
            .FirstOrDefaultAsync();

        if (memberId is null) return [];

        var grantedIds = await GrantedPersonaIdsAsync(memberId, userId);

        var personas = await ctx.Set<Persona>()
            .AsNoTracking()
            .Where(p => (p.Scope == PersonaScope.User && p.OwnerUserId == userId)
                        || (p.Scope == PersonaScope.Guild && p.OwnerGuildId == guildId && grantedIds.Contains(p.Id)))
            .ToListAsync();

        if (personas.Count == 0) return [];

        var personaIds = personas.Select(p => p.Id).ToList();

        // A persona is only usable in a guild it has been adopted into - the profile is where the
        // proxy prefix and the approval state live, so without one there is nothing to speak with.
        var profiles = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId == guildId && personaIds.Contains(p.PersonaId))
            .ToListAsync();

        var byPersona = profiles.ToDictionary(p => p.PersonaId, StringComparer.Ordinal);

        return personas
            .Where(p => byPersona.ContainsKey(p.Id))
            .Select(p =>
            {
                var profile = byPersona[p.Id];
                return new UsablePersona
                {
                    PersonaId = p.Id,
                    Scope = p.Scope,
                    OwnerUserId = p.OwnerUserId,
                    Name = PersonaDisplayGuard.EffectiveName(p, profile),
                    AvatarUrl = string.IsNullOrWhiteSpace(profile.AvatarUrl) ? p.AvatarUrl : profile.AvatarUrl,
                    Tag = profile.Tag,
                    ProxyPrefix = profile.ProxyPrefix,
                    ProxySuffix = profile.ProxySuffix,
                    IsRetired = p.IsRetired,
                    ApprovalState = profile.ApprovalState,
                    HasSpoken = p.HasSpoken,
                };
            })
            .ToList();
    }

    private async Task<List<string>> GrantedPersonaIdsAsync(string memberId, string userId)
    {
        var now = DateTimeOffset.UtcNow;

        // Expired guest roles are filtered here rather than trusting a sweep, matching
        // GuildPermissionService.GetMembershipAsync.
        var roleIds = await ctx.RoleMembers
            .AsNoTracking()
            .Where(rm => rm.MemberId == memberId && (rm.ExpiresAt == null || rm.ExpiresAt > now))
            .Select(rm => rm.RoleId)
            .ToListAsync();

        return await ctx.Set<PersonaGrant>()
            .AsNoTracking()
            .Where(g => (g.UserId != null && g.UserId == userId)
                        || (g.RoleId != null && roleIds.Contains(g.RoleId)))
            .Select(g => g.PersonaId)
            .Distinct()
            .ToListAsync();
    }

    /// <summary>
    /// Re-points a character whose reference copy is being un-adopted, promoting the oldest
    /// remaining copy that has a page.
    /// </summary>
    /// <param name="persona">The character losing a copy.</param>
    /// <param name="leavingProfileId">The profile being removed.</param>
    public Task RepointHomeAsync(Persona persona, string leavingProfileId) =>
        RepointAsync(persona, [leavingProfileId]);

    /// <summary>
    /// Re-points every character whose reference copy lives in a guild about to be deleted.
    /// <c>HomeProfileId</c> carries no foreign key, so nothing cascades it and the pointer would
    /// otherwise outlive the row it names.
    /// </summary>
    /// <param name="guildId">The guild being deleted.</param>
    public async Task RepointHomesForDeletedGuildAsync(string guildId)
    {
        var leaving = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId == guildId)
            .Select(p => p.Id)
            .ToListAsync();

        if (leaving.Count == 0) return;

        var homed = await ctx.Set<Persona>()
            .Where(p => p.HomeProfileId != null && leaving.Contains(p.HomeProfileId!))
            .ToListAsync();

        foreach (var persona in homed) await RepointAsync(persona, leaving);
    }

    /// <summary>
    /// Promotion nulls <c>UpstreamRevisionNumber</c> on every surviving copy: they hold revision
    /// numbers from a history they never shared, and §4.3 has them read as diverged rather than
    /// behind.
    /// </summary>
    private async Task RepointAsync(Persona persona, List<string> leavingProfileIds)
    {
        if (persona.HomeProfileId is null || !leavingProfileIds.Contains(persona.HomeProfileId)) return;

        var successor = await ctx.Set<PersonaGuildProfile>()
            .Where(p => p.PersonaId == persona.Id
                        && !leavingProfileIds.Contains(p.Id)
                        && p.WikiPageId != null)
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync();

        persona.HomeProfileId = successor?.Id;
        persona.UpdatedAt = DateTimeOffset.UtcNow;

        if (successor is null) return;

        var surviving = await ctx.Set<PersonaGuildProfile>()
            .Where(p => p.PersonaId == persona.Id && !leavingProfileIds.Contains(p.Id))
            .ToListAsync();

        foreach (var profile in surviving) profile.UpstreamRevisionNumber = null;
    }

    /// <summary>Whether the caller still holds a grant on a guild-scoped persona.</summary>
    public async Task<bool> HasGrantAsync(string userId, string guildId, string personaId)
    {
        var memberId = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId && m.UserId == userId)
            .Select(m => m.Id)
            .FirstOrDefaultAsync();

        if (memberId is null) return false;

        var granted = await GrantedPersonaIdsAsync(memberId, userId);
        return granted.Contains(personaId);
    }

    /// <summary>Whether this guild makes a persona wait for approval before it may speak.</summary>
    public async Task<bool> RequiresApprovalAsync(string guildId) =>
        await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => g.RequirePersonaApproval)
            .FirstOrDefaultAsync();

    /// <summary>
    /// Why the caller may not speak as this persona right now, or null. The grant is re-checked
    /// against the database rather than the cached set, so losing one stops speech at the next
    /// message instead of at the next cache expiry.
    /// </summary>
    public async Task<string?> DenyReasonAsync(string userId, string guildId, UsablePersona persona, bool requiresApproval)
    {
        if (persona.IsRetired) return "That persona is retired.";

        if (persona.Scope == PersonaScope.User)
        {
            if (persona.OwnerUserId != userId) return "That persona is not yours.";
        }
        else if (!await HasGrantAsync(userId, guildId, persona.PersonaId))
        {
            return "You no longer hold a grant on that persona.";
        }

        // The same rule as PersonaGuildProfile.MaySpeak, applied to the cached projection rather
        // than the row, because the hot path never loads the entity.
        if (requiresApproval && persona.ApprovalState != PersonaApprovalState.Approved)
            return "This guild requires a persona to be approved before it speaks.";

        return null;
    }

    /// <summary>
    /// Resolves which persona, if any, a message is being sent as. A leading backslash suppresses
    /// everything, then an explicit id wins, then a proxy prefix or suffix match, then the
    /// channel's autoproxy state, then nobody.
    /// </summary>
    public async Task<PersonaResolution> ResolveForSendAsync(PersonaSendContext context)
    {
        var content = context.Content ?? string.Empty;
        var usable = await GetUsablePersonasAsync(context.UserId, context.GuildId);
        var requiresApproval = await RequiresApprovalAsync(context.GuildId);

        // The escape beats everything, including an explicit id. A switcher selection is ambient
        // state that persists across messages; a leading backslash is typed into this one message
        // and means only one thing. Ranked below the id it would leak into the body of a message
        // that went out in character anyway - failing at both of its jobs at once.
        if (content.StartsWith('\\'))
            return new PersonaResolution(PersonaResolutionOutcome.None, content[1..]);

        if (!string.IsNullOrWhiteSpace(context.PersonaId))
        {
            var chosen = usable.FirstOrDefault(p => p.PersonaId == context.PersonaId);
            if (chosen is null)
                return new PersonaResolution(PersonaResolutionOutcome.Forbidden, content,
                    Error: "You cannot speak as that persona here.");

            if (await DenyReasonAsync(context.UserId, context.GuildId, chosen, requiresApproval) is { } denial)
                return new PersonaResolution(PersonaResolutionOutcome.Forbidden, content, Error: denial);

            var moved = await RememberStickyAsync(context, chosen.PersonaId);
            return await ResolveAsync(context.GuildId, chosen, content, moved);
        }

        if (MatchProxy(usable, content) is { } match)
        {
            if (await DenyReasonAsync(context.UserId, context.GuildId, match.Persona, requiresApproval) is { } denial)
                return new PersonaResolution(PersonaResolutionOutcome.Forbidden, content, Error: denial);

            var moved = await RememberStickyAsync(context, match.Persona.PersonaId);
            return await ResolveAsync(context.GuildId, match.Persona, match.Content, moved);
        }

        var autoproxy = await ctx.Set<PersonaAutoproxyState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ChannelId == context.ChannelId && a.UserId == context.UserId);

        var latched = autoproxy?.ResolvePersonaId();
        if (latched is null) return new PersonaResolution(PersonaResolutionOutcome.None, content);

        var auto = usable.FirstOrDefault(p => p.PersonaId == latched);

        // A persona that stopped being usable falls back to speaking as yourself rather than
        // failing the send: the user did not ask for it on this message, and losing a grant should
        // not silently eat what they typed.
        if (auto is null || await DenyReasonAsync(context.UserId, context.GuildId, auto, requiresApproval) is not null)
            return new PersonaResolution(PersonaResolutionOutcome.None, content);

        return await ResolveAsync(context.GuildId, auto, content);
    }

    /// <summary>
    /// Builds the resolution and records that the persona has now spoken, which is what turns a
    /// later delete into a retire so that Message.PersonaId never dangles.
    /// </summary>
    private async Task<PersonaResolution> ResolveAsync(
        string guildId, UsablePersona persona, string content, bool stickyMoved = false)
    {
        if (!persona.HasSpoken)
        {
            var row = await ctx.Set<Persona>().FirstOrDefaultAsync(p => p.Id == persona.PersonaId);
            if (row is not null && !row.HasSpoken)
            {
                row.HasSpoken = true;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                await InvalidateGuildAsync(guildId);
            }
        }

        var name = string.IsNullOrWhiteSpace(persona.Tag) ? persona.Name : $"{persona.Name} {persona.Tag}";

        return new PersonaResolution(
            PersonaResolutionOutcome.Resolved, content, persona.PersonaId, name, persona.AvatarUrl,
            StickyPersonaId: stickyMoved ? persona.PersonaId : null);
    }

    /// <summary>Sticky autoproxy means "whoever spoke here last", so the send path is what records it.</summary>
    /// <returns>True when the latched character actually moved.</returns>
    private async Task<bool> RememberStickyAsync(PersonaSendContext context, string personaId)
    {
        var state = await ctx.Set<PersonaAutoproxyState>()
            .FirstOrDefaultAsync(a => a.ChannelId == context.ChannelId && a.UserId == context.UserId);

        if (state is null || state.Mode != AutoproxyMode.Sticky || state.PersonaId == personaId) return false;

        state.PersonaId = personaId;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        return true;
    }

    /// <summary>
    /// The persona a prefix and suffix pair select, with both stripped from the content. Longest
    /// prefix wins so that an overlapping pair which somehow escaped the uniqueness check still
    /// resolves the same way on every message rather than by row order.
    /// </summary>
    internal static (UsablePersona Persona, string Content)? MatchProxy(
        IReadOnlyList<UsablePersona> usable, string content)
    {
        UsablePersona? best = null;
        var bestPrefixLength = -1;
        var bestContent = string.Empty;

        foreach (var candidate in usable)
        {
            var prefix = candidate.ProxyPrefix ?? string.Empty;
            var suffix = candidate.ProxySuffix ?? string.Empty;
            if (prefix.Length + suffix.Length == 0) continue;
            if (content.Length < prefix.Length + suffix.Length) continue;
            if (prefix.Length > 0 && !content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (suffix.Length > 0 && !content.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (prefix.Length <= bestPrefixLength) continue;

            best = candidate;
            bestPrefixLength = prefix.Length;
            bestContent = content[prefix.Length..(content.Length - suffix.Length)].Trim();
        }

        return best is null ? null : (best, bestContent);
    }

    /// <summary>
    /// Whether two prefix and suffix pairs can both fire on the same message. Not equality: <c>M</c>
    /// and <c>M:</c> both match "M: hello", and a prefix resolving to two personas is a silent
    /// mis-attribution rather than an error.
    /// </summary>
    public static bool AffixesCollide(string? prefixA, string? suffixA, string? prefixB, string? suffixB)
    {
        var pa = prefixA ?? string.Empty;
        var sa = suffixA ?? string.Empty;
        var pb = prefixB ?? string.Empty;
        var sb = suffixB ?? string.Empty;

        if (pa.Length + sa.Length == 0 || pb.Length + sb.Length == 0) return false;

        var prefixesOverlap = pa.StartsWith(pb, StringComparison.OrdinalIgnoreCase)
                              || pb.StartsWith(pa, StringComparison.OrdinalIgnoreCase);
        if (!prefixesOverlap) return false;

        return sa.Length == 0 || sb.Length == 0
                              || sa.EndsWith(sb, StringComparison.OrdinalIgnoreCase)
                              || sb.EndsWith(sa, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other persona <paramref name="prefix"/> would also select, across every set that could
    /// reach it in this guild. The check spans two tables and so cannot be a unique index; this is
    /// the one place it is written, shared by the profile edit and the grant paths.
    /// </summary>
    public async Task<PersonaCollision?> FindProxyCollisionAsync(
        string guildId, Persona persona, string? prefix, string? suffix)
    {
        if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix)) return null;

        var candidates = new Dictionary<string, UsablePersona>(StringComparer.Ordinal);

        if (persona.Scope == PersonaScope.User)
        {
            if (string.IsNullOrWhiteSpace(persona.OwnerUserId)) return null;
            foreach (var reachable in await GetUsablePersonasAsync(persona.OwnerUserId, guildId))
                candidates[reachable.PersonaId] = reachable;
        }
        else
        {
            // Two guild personas clash for everyone, whether or not anybody holds a grant yet.
            foreach (var other in await GuildScopedAffixesAsync(guildId))
                candidates[other.PersonaId] = other;

            foreach (var granteeId in await GranteeUserIdsAsync(guildId, persona.Id))
            foreach (var reachable in await GetUsablePersonasAsync(granteeId, guildId))
                candidates[reachable.PersonaId] = reachable;
        }

        candidates.Remove(persona.Id);

        foreach (var candidate in candidates.Values)
        {
            if (AffixesCollide(prefix, suffix, candidate.ProxyPrefix, candidate.ProxySuffix))
                return new PersonaCollision(candidate.PersonaId, candidate.Name);
        }

        return null;
    }

    /// <summary>
    /// The mirror of <see cref="FindProxyCollisionAsync"/> for a grant, which widens who can reach a
    /// guild persona and so can introduce a collision without any prefix being edited.
    /// </summary>
    public async Task<PersonaCollision?> FindGrantProxyCollisionAsync(
        string guildId, string personaId, IReadOnlyCollection<string> newGranteeUserIds)
    {
        var affixes = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId == guildId && p.PersonaId == personaId)
            .Select(p => new { p.ProxyPrefix, p.ProxySuffix })
            .FirstOrDefaultAsync();

        if (affixes is null || (string.IsNullOrEmpty(affixes.ProxyPrefix) && string.IsNullOrEmpty(affixes.ProxySuffix)))
            return null;

        foreach (var granteeId in newGranteeUserIds)
        {
            foreach (var reachable in await GetUsablePersonasAsync(granteeId, guildId))
            {
                if (reachable.PersonaId == personaId) continue;
                if (AffixesCollide(affixes.ProxyPrefix, affixes.ProxySuffix, reachable.ProxyPrefix, reachable.ProxySuffix))
                    return new PersonaCollision(reachable.PersonaId, reachable.Name);
            }
        }

        return null;
    }

    /// <summary>Every user a grant on this persona currently reaches.</summary>
    public async Task<List<string>> GranteeUserIdsAsync(string guildId, string personaId)
    {
        var grants = await ctx.Set<PersonaGrant>()
            .AsNoTracking()
            .Where(g => g.PersonaId == personaId)
            .Select(g => new { g.RoleId, g.UserId })
            .ToListAsync();

        if (grants.Count == 0) return [];

        var userIds = grants
            .Where(g => !string.IsNullOrWhiteSpace(g.UserId))
            .Select(g => g.UserId!)
            .ToHashSet(StringComparer.Ordinal);

        var roleIds = grants
            .Where(g => !string.IsNullOrWhiteSpace(g.RoleId))
            .Select(g => g.RoleId!)
            .ToList();

        if (roleIds.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var fromRoles = await ctx.RoleMembers
                .AsNoTracking()
                .Where(rm => roleIds.Contains(rm.RoleId) && (rm.ExpiresAt == null || rm.ExpiresAt > now))
                .Join(ctx.GuildMembers.AsNoTracking().Where(m => m.GuildId == guildId),
                    rm => rm.MemberId, m => m.Id, (_, m) => m.UserId)
                .Distinct()
                .ToListAsync();

            foreach (var userId in fromRoles) userIds.Add(userId);
        }

        return userIds.ToList();
    }

    private async Task<List<UsablePersona>> GuildScopedAffixesAsync(string guildId)
    {
        var rows = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.GuildId == guildId)
            .Join(ctx.Set<Persona>().AsNoTracking().Where(p => p.Scope == PersonaScope.Guild),
                profile => profile.PersonaId, persona => persona.Id,
                (profile, persona) => new
                {
                    persona.Id,
                    persona.Name,
                    profile.DisplayName,
                    profile.ProxyPrefix,
                    profile.ProxySuffix,
                })
            .ToListAsync();

        return rows.Select(r => new UsablePersona
        {
            PersonaId = r.Id,
            Scope = PersonaScope.Guild,
            Name = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Name : r.DisplayName,
            ProxyPrefix = r.ProxyPrefix,
            ProxySuffix = r.ProxySuffix,
        }).ToList();
    }
}
