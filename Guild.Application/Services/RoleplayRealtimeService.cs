using Echo.Realtime;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The roleplay surface's hub events: characters, adoptions, the approval queue, character pages,
/// dice and autoproxy. Scenes publish their own from <see cref="SceneService"/>, which already
/// owns the turn state the payloads are read from.
///
/// Three audiences, and which one an event gets is a privacy decision rather than a performance
/// one: the guild's present members for anything already visible on the cast route, the character's
/// players plus the module's permission holders for a review, and one user for their own state.
/// </summary>
public class RoleplayRealtimeService(
    MicroserviceContext ctx,
    GuildHydrateService hydrate,
    ChannelAudienceService audience,
    PersonaMentionService mentions,
    ModulePermissionHolderService holders,
    IHubContext<EchoRealtimeHub> hub)
{
    public const string PersonaCreatedEvent = "guild.PersonaCreated";
    public const string PersonaUpdatedEvent = "guild.PersonaUpdated";
    public const string PersonaDeletedEvent = "guild.PersonaDeleted";
    public const string PersonaAdoptedEvent = "guild.PersonaAdopted";
    public const string PersonaUnadoptedEvent = "guild.PersonaUnadopted";
    public const string PersonaProfileChangedEvent = "guild.PersonaProfileChanged";
    public const string ReviewRequestedEvent = "guild.PersonaReviewRequested";
    public const string ReviewCompletedEvent = "guild.PersonaReviewCompleted";
    public const string GrantCreatedEvent = "guild.PersonaGrantCreated";
    public const string GrantDeletedEvent = "guild.PersonaGrantDeleted";
    public const string PageCreatedEvent = "guild.PersonaPageCreated";
    public const string PagePulledEvent = "guild.PersonaPagePulled";
    public const string DiceRolledEvent = "guild.DiceRolled";
    public const string AutoproxyChangedEvent = "guild.AutoproxyChanged";

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Characters
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A character now exists. A guild-owned one is announced to the guild that owns it; a personal
    /// one has no guild yet and goes to its owner alone, which is what keeps a second device's
    /// switcher current.
    /// </summary>
    /// <param name="persona">The new character.</param>
    public async Task PersonaCreatedAsync(Persona persona)
    {
        var payload = Describe(persona, persona.OwnerGuildId);

        if (persona.Scope == PersonaScope.Guild && persona.OwnerGuildId is { } guildId)
        {
            await BroadcastGuildAsync(guildId, PersonaCreatedEvent, payload);
            return;
        }

        if (persona.OwnerUserId is { } ownerId)
            await hub.Clients.User(ownerId).SendAsync(PersonaCreatedEvent, payload);
    }

    /// <summary>
    /// A character's own fields changed, in every guild that has adopted it - the name and avatar
    /// it renders under changed everywhere at once. Colour, pronouns and bio travel with it so a
    /// client can patch its cache rather than drop it.
    /// </summary>
    /// <param name="persona">The edited character.</param>
    public async Task PersonaUpdatedAsync(Persona persona) =>
        await FanOutAsync(persona, PersonaUpdatedEvent, guildId => Describe(persona, guildId));

    /// <summary>A character was deleted, or retired because it had already spoken.</summary>
    /// <param name="persona">The character.</param>
    /// <param name="retired">True when it was retired rather than removed.</param>
    /// <param name="guildIds">The guilds it was adopted into, resolved before the row went.</param>
    public async Task PersonaDeletedAsync(Persona persona, bool retired, IReadOnlyList<string> guildIds)
    {
        foreach (var guildId in guildIds)
        {
            await BroadcastGuildAsync(guildId, PersonaDeletedEvent, new
            {
                GuildId = guildId,
                PersonaId = persona.Id,
                Retired = retired,
            });
        }

        if (persona.OwnerUserId is { } ownerId)
        {
            await hub.Clients.User(ownerId).SendAsync(PersonaDeletedEvent, new
            {
                GuildId = (string?)null,
                PersonaId = persona.Id,
                Retired = retired,
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Adoption and the approval queue
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A character joined this guild's cast.</summary>
    /// <param name="persona">The character.</param>
    /// <param name="profile">Its brand new profile here.</param>
    /// <param name="canSpeak">Whether the profile's own state lets it be played.</param>
    public async Task PersonaAdoptedAsync(Persona persona, PersonaGuildProfile profile, bool canSpeak) =>
        await BroadcastGuildAsync(profile.GuildId, PersonaAdoptedEvent, new
        {
            profile.GuildId,
            profile.PersonaId,
            Name = PersonaDisplayGuard.EffectiveName(persona, profile),
            AvatarUrl = string.IsNullOrWhiteSpace(profile.AvatarUrl) ? persona.AvatarUrl : profile.AvatarUrl,
            persona.Color,
            profile.Tag,
            profile.WikiPageId,
            profile.ApprovalState,
            CanSpeak = canSpeak,
        });

    /// <summary>
    /// A profile's approval state or display overrides changed. The guild-wide event kept from the
    /// first cut of this surface, and the one a cast list follows.
    /// </summary>
    /// <param name="profile">The profile as it now stands.</param>
    /// <param name="canSpeak">The profile's own state, not a per-recipient answer.</param>
    public async Task ProfileChangedAsync(PersonaGuildProfile profile, bool canSpeak) =>
        await BroadcastGuildAsync(profile.GuildId, PersonaProfileChangedEvent, new
        {
            profile.GuildId,
            profile.PersonaId,
            profile.ApprovalState,
            CanSpeak = canSpeak,
        });

    /// <summary>A character left this guild's cast.</summary>
    /// <param name="guildId">The guild it was adopted into.</param>
    /// <param name="personaId">The character.</param>
    public async Task PersonaUnadoptedAsync(string guildId, string personaId) =>
        await BroadcastGuildAsync(guildId, PersonaUnadoptedEvent, new
        {
            GuildId = guildId,
            PersonaId = personaId,
        });

    /// <summary>
    /// A character is waiting on a review, told to the reviewers and to whoever answers for it. It
    /// does not go to the guild: a queue is a moderation surface, and the guild-wide profile event
    /// already says the state changed.
    /// </summary>
    /// <param name="persona">The character.</param>
    /// <param name="profile">Its profile here.</param>
    /// <param name="isResubmission">True when it had previously been sent back.</param>
    public async Task ReviewRequestedAsync(
        Persona persona, PersonaGuildProfile profile, bool isResubmission)
    {
        var recipients = await ReviewAudienceAsync(profile);
        if (recipients.Count == 0) return;

        await hub.Clients.Users(recipients).SendAsync(ReviewRequestedEvent, new
        {
            profile.GuildId,
            profile.PersonaId,
            Name = PersonaDisplayGuard.EffectiveName(persona, profile),
            profile.WikiPageId,
            profile.ApprovalState,
            IsResubmission = isResubmission,
            SubmittedAt = profile.UpdatedAt,
        });
    }

    /// <summary>
    /// A review was decided. The reason is reviewer feedback for the character's players, so it
    /// travels on this event and never on the guild-wide one.
    /// </summary>
    /// <param name="persona">The character.</param>
    /// <param name="profile">Its profile here.</param>
    /// <param name="reviewerUserId">Who decided it.</param>
    /// <param name="canSpeak">Whether the character may now be played.</param>
    public async Task ReviewCompletedAsync(
        Persona persona, PersonaGuildProfile profile, string reviewerUserId, bool canSpeak)
    {
        var recipients = await ReviewAudienceAsync(profile);
        if (recipients.Count == 0) return;

        await hub.Clients.Users(recipients).SendAsync(ReviewCompletedEvent, new
        {
            profile.GuildId,
            profile.PersonaId,
            Name = PersonaDisplayGuard.EffectiveName(persona, profile),
            profile.ApprovalState,
            Approved = profile.ApprovalState == PersonaApprovalState.Approved,
            ReviewedByUserId = reviewerUserId,
            ReviewedAt = profile.UpdatedAt,
            Reason = profile.ChangesRequestedReason,
            CanSpeak = canSpeak,
        });
    }

    /// <summary>
    /// A grant on a guild-owned character was created or revoked, told to whoever manages
    /// characters here and to the user it names. Who may speak as a character is what the grant
    /// list answers, and that route is gated - so this one is too.
    /// </summary>
    /// <param name="eventName">Either <see cref="GrantCreatedEvent"/> or <see cref="GrantDeletedEvent"/>.</param>
    /// <param name="guildId">The guild the character belongs to.</param>
    /// <param name="grant">The grant.</param>
    public async Task GrantChangedAsync(string eventName, string guildId, PersonaGrant grant)
    {
        var recipients = new HashSet<string>(
            await holders.HoldersAsync(guildId, ModulePermissions.ManageAnyPersona),
            StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(grant.UserId)) recipients.Add(grant.UserId);

        if (recipients.Count == 0) return;

        await hub.Clients.Users(recipients.ToList()).SendAsync(eventName, new
        {
            GuildId = guildId,
            grant.PersonaId,
            GrantId = grant.Id,
            grant.RoleId,
            grant.UserId,
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Character pages
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// This guild's copy of a character page now exists. It arrives alongside
    /// <c>guild.WikiPageCreated</c>, which says a page appeared but not which character it is for.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="personaId">The character the page belongs to.</param>
    /// <param name="page">The page.</param>
    public async Task PageCreatedAsync(string guildId, string personaId, WikiPage page) =>
        await BroadcastGuildAsync(guildId, PageCreatedEvent, new
        {
            GuildId = guildId,
            PersonaId = personaId,
            PageId = page.Id,
            page.Title,
            page.CategoryId,
        });

    /// <summary>
    /// A character page was merged from its reference copy. A preview writes nothing and announces
    /// nothing.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="strategy">The strategy that was applied.</param>
    /// <param name="pull">What the merge did.</param>
    public async Task PagePulledAsync(
        string guildId, PersonaPagePullStrategy strategy, PersonaPagePullDto pull)
    {
        if (!pull.Applied) return;

        await BroadcastGuildAsync(guildId, PagePulledEvent, new
        {
            GuildId = guildId,
            pull.PersonaId,
            pull.PageId,
            Strategy = strategy,
            pull.UpstreamState,
            pull.UpstreamRevisionNumber,
            pull.ReferenceRevisionNumber,
            ConflictCount = pull.Conflicts.Count,
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Dice and autoproxy
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A roll landed, to whoever can see the channel it landed in. The faces travel with it so a
    /// client can animate the roll rather than parse the message embed back out.
    /// </summary>
    /// <param name="roll">The recorded roll.</param>
    /// <param name="breakdown">The rendered arithmetic.</param>
    public async Task DiceRolledAsync(DiceRoll roll, string breakdown)
    {
        var presence = await hydrate.GetGuildPresenceAsync(roll.GuildId);
        if (presence.Count == 0) return;

        var viewers = await audience.FilterToViewersAsync(roll.ChannelId, presence.Select(p => p.UserId));
        if (viewers.Count == 0) return;

        await hub.Clients.Users(viewers).SendAsync(DiceRolledEvent, new
        {
            roll.GuildId,
            roll.ChannelId,
            RollId = roll.Id,
            roll.MessageId,
            // Withheld for a roll made in character: naming the account behind a character is the
            // one thing this whole surface is built not to do.
            RollerUserId = roll.PersonaId is null ? roll.RollerUserId : null,
            roll.PersonaId,
            roll.Expression,
            roll.Total,
            Breakdown = breakdown,
            roll.Reason,
            roll.Visibility,
            roll.CreatedAt,
        });
    }

    /// <summary>
    /// The caller's autoproxy state for one channel, to the caller alone. Under Sticky the send
    /// path moves this without anybody asking, so a second device shows a stale "speaking as"
    /// until it hears this.
    /// </summary>
    /// <param name="userId">Whose state it is.</param>
    /// <param name="guildId">The guild.</param>
    /// <param name="channelId">The channel the state applies to.</param>
    /// <param name="mode">The mode now in force.</param>
    /// <param name="personaId">The latched character, where there is one.</param>
    public async Task AutoproxyChangedAsync(
        string userId, string guildId, string channelId, AutoproxyMode mode, string? personaId) =>
        await hub.Clients.User(userId).SendAsync(AutoproxyChangedEvent, new
        {
            GuildId = guildId,
            ChannelId = channelId,
            Mode = mode,
            PersonaId = personaId,
        });

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The character as every guild that adopted it should redraw it.</summary>
    private static object Describe(Persona persona, string? guildId) => new
    {
        GuildId = guildId,
        PersonaId = persona.Id,
        persona.Scope,
        persona.Name,
        persona.AvatarUrl,
        persona.Pronouns,
        persona.Color,
        persona.ShortBio,
        persona.IsRetired,
        persona.UpdatedAt,
    };

    /// <summary>
    /// Sends one persona event to every guild the character is adopted into, and to its owner where
    /// it has one - a personal character adopted nowhere still has a switcher to keep current.
    /// </summary>
    private async Task FanOutAsync(Persona persona, string eventName, Func<string?, object> payload)
    {
        var guildIds = await ctx.Set<PersonaGuildProfile>()
            .AsNoTracking()
            .Where(p => p.PersonaId == persona.Id)
            .Select(p => p.GuildId)
            .Distinct()
            .ToListAsync();

        foreach (var guildId in guildIds)
            await BroadcastGuildAsync(guildId, eventName, payload(guildId));

        if (persona.OwnerUserId is { } ownerId)
            await hub.Clients.User(ownerId).SendAsync(eventName, payload(null));
    }

    /// <summary>Whoever reviews characters here, plus whoever answers for this one.</summary>
    private async Task<List<string>> ReviewAudienceAsync(PersonaGuildProfile profile)
    {
        var recipients = new HashSet<string>(
            await holders.HoldersAsync(profile.GuildId, ModulePermissions.ApprovePersonas),
            StringComparer.Ordinal);

        var players = await mentions.OwnersByPersonaAsync(profile.GuildId, [profile.PersonaId]);
        if (players.TryGetValue(profile.PersonaId, out var found)) recipients.UnionWith(found);

        return recipients.ToList();
    }

    private async Task BroadcastGuildAsync(string guildId, string eventName, object payload)
    {
        var presence = await hydrate.GetGuildPresenceAsync(guildId);
        if (presence.Count == 0) return;

        await hub.Clients.Users(presence.Select(p => p.UserId).ToList()).SendAsync(eventName, payload);
    }
}
