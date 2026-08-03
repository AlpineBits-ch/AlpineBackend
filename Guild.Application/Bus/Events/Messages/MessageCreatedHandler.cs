using Echo.Realtime;

using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Bus.Events.Messages;

public class MessageCreatedHandler
{
    private string GetChannelKey(string channelId)
    {
        return $"channel:{channelId}:guild";
    }
    public async Task Handle(MessageCreatedForChannel message, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, IMessageBus bus, ILogger<MessageCreatedHandler> logger,
        NotificationResolutionService notificationService, ChannelAudienceService audience)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var cachedGuildId = await cache.GetStringAsync(channelKey);
        string guildId;
        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
           var channel =  context.Channels.Where(c => c.Id == message.ChannelId).Select(c => c.GuildId).FirstOrDefault();
           if (channel is null)
           {
               logger.LogWarning($"Channel with ID {message.ChannelId} not found in context");
               return;
           }
           guildId = channel;
           cachedGuildId = guildId;
           await cache.SetStringAsync(channelKey, guildId);
        }

        var presence = await service.GetGuildPresenceAsync(cachedGuildId);

        // Guild presence is guild-wide; this event is channel-scoped and carries the message
        // content, so the audience has to be narrowed to members who can actually see the channel.
        // Without this, every online member of the guild received every private channel's messages
        // in plaintext over the hub - bypassing the ViewChannel check the REST history endpoint
        // enforces for the same content.
        var viewerIds = await audience.FilterToViewersAsync(
            message.ChannelId, presence.Select(p => p.UserId).Except([message.AuthorId]));

        await hub.Clients.Users(viewerIds).SendAsync("guild.MessageCreated", message);

        await TouchChannelActivityAsync(message.ChannelId, message.CreatedAt, message.MessageId, context);

        // Bots.Application can't join the SignalR/Redis backplane the hub broadcast above rides
        // on, so it gets its own event - carrying the GuildId this handler just resolved, since
        // the raw MessageCreatedForChannel event doesn't have it.
        await bus.PublishAsync(new MessageCreatedForBots
        {
            GuildId = cachedGuildId,
            ChannelId = message.ChannelId,
            MessageId = message.MessageId,
            Content = message.Content,
            AuthorId = message.AuthorId,
            EncryptionState = message.EncryptionState,
            EmbedsJson = message.EmbedsJson,
            Type = message.Type,
            SystemMessageVariant = message.SystemMessageVariant,
        });

        var mentioned = await ResolveMentionedMembersAsync(message, cachedGuildId, presence, viewerIds, context);

        await RecordBroadcastMentionsAsync(message, context);

        await PublishMentionIndexAsync(message, cachedGuildId, mentioned, bus);

        await PushMentionAddedAsync(message, cachedGuildId, mentioned, hub);

        await PublishPushRecipientsAsync(message, cachedGuildId, presence, mentioned, context, notificationService, bus);
    }

    /// <summary>A mentioned member. Both ids are needed: the badge and push paths key on the
    /// member, the mention index keys on the user.</summary>
    private sealed record MentionedMember(string MemberId, string UserId);

    /// <summary>
    /// Who a message mentioned, split by how.
    ///
    /// <para>The split is not cosmetic. SuppressRoleMentions and SuppressEveryone let a member opt
    /// out of one kind without opting out of a direct @, and only Direct and Here are written to the
    /// per-user mention index - @everyone and @role are one broadcast row each, evaluated at read
    /// time, so their cost does not scale with the guild.</para>
    /// </summary>
    private sealed record MentionedMembers(
        List<MentionedMember> Direct,
        HashSet<string> ByRole,
        List<MentionedMember> Here)
    {
        /// <summary>Everyone individually named by this message - what goes in the index.</summary>
        public IEnumerable<MentionedMember> Indexable => Direct.Concat(Here).DistinctBy(m => m.UserId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves who this message mentioned - direct @user, members holding an @role-mentioned role,
    /// the whole guild (@everyone), or the people actually present (@here). Guild owns all of this
    /// data, so each is one local query.
    ///
    /// The author is excluded throughout: @-ing yourself, or being swept up by your own @everyone,
    /// is not a mention. The realtime broadcast above has always excluded them.
    /// </summary>
    private static async Task<MentionedMembers> ResolveMentionedMembersAsync(
        MessageCreatedForChannel message,
        string guildId,
        IReadOnlyCollection<MemberPresenceState> presence,
        IReadOnlyCollection<string> viewerIds,
        MicroserviceContext context)
    {
        var direct = new List<MentionedMember>();
        var byRole = new HashSet<string>(StringComparer.Ordinal);
        var here = new List<MentionedMember>();

        if (message.Mentions.Count > 0)
        {
            direct.AddRange(await context.GuildMembers
                .AsNoTracking()
                .Where(m => m.GuildId == guildId
                            && message.Mentions.Contains(m.UserId)
                            && m.UserId != message.AuthorId)
                .Select(m => new MentionedMember(m.Id, m.UserId))
                .ToListAsync());
        }

        if (message.RoleMentions.Count > 0)
        {
            byRole.UnionWith(await context.RoleMembers
                .AsNoTracking()
                .Where(rm => message.RoleMentions.Contains(rm.RoleId)
                             && rm.Member.UserId != message.AuthorId)
                .Select(rm => rm.MemberId)
                .ToListAsync());
        }

        // Deliberately no @everyone expansion. It is recorded as a single broadcast row instead
        // (RecordBroadcastMentionsAsync), because its recipients are reconstructable at read time
        // from join dates - so materializing them here would put the write cost of one ping in
        // proportion to the size of the guild. Same for @role.

        if (message.MentionsHere)
        {
            // Two filters the old code did not apply, both of which produced mentions for people
            // the message never reached:
            //
            // Visibility - presence is guild-wide, so an @here in a private channel bumped the
            // badge of every online member of the guild, including those without ViewChannel. Same
            // leak ChannelAudienceService was added to close for the message broadcast itself;
            // viewerIds is that filter's output, already computed above.
            //
            // Status - "here" means at the keyboard. Presence covers everyone holding a live
            // connection, so idle, do-not-disturb and invisible members were all being treated as
            // present.
            var viewers = viewerIds.ToHashSet(StringComparer.Ordinal);

            here.AddRange(presence
                .Where(p => viewers.Contains(p.UserId) && IsOnline(p.Status))
                .Select(p => new MentionedMember(p.MemberId, p.UserId)));
        }

        return new MentionedMembers(direct, byRole, here);
    }

    /// <summary>Whether a presence entry counts as "here". Statuses are stored as
    /// <see cref="OnlineStatus"/> member names; anything unrecognised is treated as absent, which
    /// is the same direction GuildController's presence projection falls back in.</summary>
    private static bool IsOnline(string? status) =>
        Enum.TryParse<OnlineStatus>(status, ignoreCase: true, out var parsed) && parsed == OnlineStatus.Online;

    /// <summary>
    /// Hands the individually-named recipients to Messaging's mention index, chunked and off the
    /// hot path.
    ///
    /// <para>Only direct and @here mentions travel here. @everyone and @role are one row each on
    /// this side, so the size of this command is set by the message, not by the guild - which is the
    /// whole point.</para>
    ///
    /// <para>Published rather than awaited inline so the message-create path returns as soon as the
    /// message is stored, and so the fan-out inherits Wolverine's retry and error-queue handling.
    /// The handler on the far side is idempotent by primary key, which is what makes that safe.</para>
    /// </summary>
    private static async Task PublishMentionIndexAsync(
        MessageCreatedForChannel message, string guildId, MentionedMembers mentioned, IMessageBus bus)
    {
        var recipients = mentioned.Indexable.ToList();
        if (recipients.Count == 0) return;

        var directUserIds = mentioned.Direct.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);

        foreach (var chunk in recipients.Chunk(IndexMentionsCommand.MaxRecipients))
        {
            await bus.PublishAsync(new IndexMentionsCommand
            {
                MessageId = message.MessageId,
                CreatedAt = message.CreatedAt,
                ContextId = message.ChannelId,
                GuildId = guildId,
                ChannelId = message.ChannelId,
                AuthorId = message.AuthorId,
                Recipients = chunk
                    .Select(m => new MentionRecipient
                    {
                        UserId = m.UserId,
                        // Direct wins when both apply - being named outright is the more specific
                        // fact, and it is the one the client renders differently.
                        Kind = directUserIds.Contains(m.UserId)
                            ? nameof(MentionKind.Direct)
                            : nameof(MentionKind.Here),
                    })
                    .ToList(),
            });
        }
    }

    /// <summary>
    /// Tells the mentioned users' open clients, so a badge lights up without polling.
    ///
    /// <para>Free: the recipient set was already computed for the index, and both sets were already
    /// filtered to channel viewers. Only individually-named recipients get this - a client that
    /// receives the message itself can work out an @everyone for itself from the flags on it, which
    /// is exactly how Discord's client does it.</para>
    /// </summary>
    private static async Task PushMentionAddedAsync(
        MessageCreatedForChannel message,
        string guildId,
        MentionedMembers mentioned,
        IHubContext<EchoRealtimeHub> hub)
    {
        var directUserIds = mentioned.Direct.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        var recipients = mentioned.Indexable.ToList();
        if (recipients.Count == 0) return;

        foreach (var group in recipients.GroupBy(r => directUserIds.Contains(r.UserId)))
        {
            await hub.Clients.Users(group.Select(r => r.UserId).ToList()).SendAsync("inbox.MentionAdded", new
            {
                MessageId = message.MessageId,
                ChannelId = message.ChannelId,
                GuildId = guildId,
                ConversationId = (string?)null,
                AuthorId = message.AuthorId,
                Kind = group.Key ? nameof(MentionKind.Direct) : nameof(MentionKind.Here),
                CreatedAt = message.CreatedAt,
            });
        }
    }

    /// <summary>
    /// Works out who should get a phone notification for this message and hands the list to
    /// Messaging, which owns Firebase.
    ///
    /// Until this existed, guild channels produced no push at all - only DMs did - so a mention in
    /// a server was invisible unless the app happened to be open.
    /// </summary>
    private static async Task PublishPushRecipientsAsync(
        MessageCreatedForChannel message,
        string guildId,
        IReadOnlyCollection<MemberPresenceState> presence,
        MentionedMembers mentioned,
        MicroserviceContext context,
        NotificationResolutionService notificationService,
        IMessageBus bus)
    {
        // Everyone who could conceivably be notified: whoever the message named, plus whoever's
        // settings say "tell me about everything" - that case is precisely what the mention set
        // does not cover.
        //
        // This used to load every member of the guild on *every* message, mentions or not, and hand
        // all of their ids to two IN-list queries. NotifiableCandidatesAsync narrows it to the
        // members who can actually clear ShouldNotify, which for a guild whose default is
        // OnlyMentions is just the ones this message named.
        // An @everyone genuinely does have to notify everyone, so the candidate set for one is the
        // membership - that cost is the setting's meaning, not an accident of modelling, and it is
        // paid here on the push path only. The mention *index* never pays it.
        var namedMemberIds = mentioned.Direct.Select(m => m.MemberId)
            .Concat(mentioned.ByRole)
            .Concat(mentioned.Here.Select(m => m.MemberId))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = await notificationService.NotifiableCandidatesAsync(
            guildId, message.ChannelId, namedMemberIds, message.AuthorId,
            includeEveryMember: message.MentionsEveryone);

        if (candidates.Count == 0) return;

        // Connected members already received the message over the realtime hub a few lines above;
        // pushing to them as well is how a notification arrives on the phone for something the
        // user is actively looking at on their desktop.
        var connectedUserIds = presence.Select(p => p.UserId).ToHashSet(StringComparer.Ordinal);

        var resolved = await notificationService.ResolveForChannelAsync(
            message.ChannelId, candidates.Select(c => c.MemberId).ToList());

        var directMemberIds = mentioned.Direct.Select(m => m.MemberId).ToHashSet(StringComparer.Ordinal);
        var hereMemberIds = mentioned.Here.Select(m => m.MemberId).ToHashSet(StringComparer.Ordinal);

        var recipients = new List<string>();
        foreach (var candidate in candidates)
        {
            if (connectedUserIds.Contains(candidate.UserId)) continue;
            if (!resolved.TryGetValue(candidate.MemberId, out var settings)) continue;
            if (!settings.MobilePush) continue;

            var isDirectMention = directMemberIds.Contains(candidate.MemberId);
            var isRoleMention = mentioned.ByRole.Contains(candidate.MemberId);

            // @here resolves against the presence snapshot taken when this message was handled, so
            // it only counts for members who were actually here. @everyone counts for everyone.
            var isEveryoneMention = message.MentionsEveryone || hereMemberIds.Contains(candidate.MemberId);

            if (settings.ShouldNotify(isDirectMention, isRoleMention, isEveryoneMention))
                recipients.Add(candidate.UserId);
        }

        if (recipients.Count == 0) return;

        await bus.PublishAsync(new ChannelPushRequested
        {
            GuildId = guildId,
            ChannelId = message.ChannelId,
            MessageId = message.MessageId,
            UserIds = recipients,
            AuthorId = message.AuthorId,
            Content = message.Content,
            IsEncrypted = message.EncryptionState != Guild.Contracts.Bus.Events.MessageEncryptionState.Plain,
            MlsGeneration = message.MlsGeneration,
        });
    }

    /// <summary>Denormalizes the head of the channel onto its row: when it last saw activity, which
    /// message was last, and how many there have been.
    ///
    /// <para>Two readers. The forum post list sorts on LastActivityAt, and that sort has to be a
    /// plain index scan - Messaging owns the messages, so the alternative is a cross-service call
    /// per post per page. The inbox compares the same field against each member's read cursor to
    /// decide what is unread, which is why this is now maintained for every channel type rather
    /// than only for threads.</para>
    ///
    /// <para>The timestamp comes off the event, not from UtcNow: it is the message's own stored
    /// CreatedAt, and taking a fresh reading here would put the channel head slightly ahead of the
    /// message a cursor read resolves against.</para>
    ///
    /// <para>Auto-archive stays thread-only - a text channel has no deadline to slide.</para></summary>
    private static async Task TouchChannelActivityAsync(
        string channelId, DateTimeOffset messageCreatedAt, string messageId, MicroserviceContext context)
    {
        var channel = await context.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        channel.LastActivityAt = messageCreatedAt;
        channel.LastMessageId = messageId;
        channel.MessageCount++;

        // Slides against the stored window, not against the previous deadline - the duration is
        // fixed for the post's lifetime, only the deadline moves.
        if (channel.Type == ChannelType.Thread && channel.AutoArchiveMinutes is > 0)
            channel.AutoArchiveAt = messageCreatedAt.AddMinutes(channel.AutoArchiveMinutes.Value);
    }

    /// <summary>
    /// Records `@everyone`/`@here`/`@role` as one row per ping rather than one per recipient.
    ///
    /// <para>`@here` is the exception and does <b>not</b> come through here - see
    /// <see cref="ResolveMentionedMembersAsync"/>. Its audience is the presence snapshot, which is
    /// gone by the time anything reads this, so it is the one mention that has to be materialised
    /// while the answer still exists.</para>
    ///
    /// <para>Idempotent by the (MessageId, RoleId) unique index: a retried handler re-runs this and
    /// must not double the ping, because the read path counts rows.</para>
    /// </summary>
    private static async Task RecordBroadcastMentionsAsync(
        MessageCreatedForChannel message, MicroserviceContext context)
    {
        if (!message.MentionsEveryone && message.RoleMentions.Count == 0) return;

        var existing = await context.ChannelBroadcastMentions
            .AsNoTracking()
            .Where(b => b.MessageId == message.MessageId)
            .Select(b => b.RoleId)
            .ToListAsync();

        var seen = existing.ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;

        void Record(BroadcastMentionKind kind, string? roleId)
        {
            if (!seen.Add(roleId)) return;

            context.ChannelBroadcastMentions.Add(new ChannelBroadcastMention
            {
                Id = ChannelBroadcastMention.GenerateId(),
                CreatedAt = now,
                UpdatedAt = now,
                ChannelId = message.ChannelId,
                MessageId = message.MessageId,
                MessageCreatedAt = message.CreatedAt,
                AuthorId = message.AuthorId,
                Kind = kind,
                RoleId = roleId,
            });
        }

        if (message.MentionsEveryone) Record(BroadcastMentionKind.Everyone, null);

        foreach (var roleId in message.RoleMentions.Distinct(StringComparer.Ordinal))
        {
            Record(BroadcastMentionKind.Role, roleId);
        }
    }
}