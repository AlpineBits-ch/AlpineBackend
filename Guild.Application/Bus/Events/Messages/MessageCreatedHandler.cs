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
        NotificationResolutionService notificationService, ChannelAudienceService audience,
        BlockCache blocks, PrivacySettingsCache privacySettings, PersonaMentionService personaMentions,
        SceneService scenes, SceneJoinService joins)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var cachedGuildId = await cache.GetStringAsync(channelKey);
        string guildId;
        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
           var owningGuildId =  context.Channels.Where(c => c.Id == message.ChannelId).Select(c => c.GuildId).FirstOrDefault();
           if (owningGuildId is null)
           {
               logger.LogWarning($"Channel with ID {message.ChannelId} not found in context");
               return;
           }
           guildId = owningGuildId;
           cachedGuildId = guildId;
           await cache.SetStringAsync(channelKey, guildId);
        }

        var presence = await service.GetGuildPresenceAsync(cachedGuildId);

        // Guild presence is guild-wide; this event is channel-scoped and carries the message
        // content, so the audience has to be narrowed to members who can actually see the channel.
        var viewerIds = await audience.FilterToViewersAsync(
            message.ChannelId, presence.Select(p => p.UserId).Except([message.AuthorId]));

        // The author is in the broadcast audience, and excluded from everything below it.
        var broadcastIds = await audience.FilterToViewersAsync(
            message.ChannelId, presence.Select(p => p.UserId).Append(message.AuthorId).Distinct());

        await hub.Clients.Users(broadcastIds).SendAsync("guild.MessageCreated", message);

        var channel = await TouchChannelActivityAsync(
            message.ChannelId, message.CreatedAt, message.MessageId, context);

        // The turn moving on its own is what makes a scene feel like play rather than
        // administration. One comparison on a row this handler already has decides it, so the
        // instance's whole message flow pays nothing for scenes it does not have.
        // The scene's own log lines are not posts: counting them would inflate the chronicle, and
        // the join line carries the character that just spoke, which would move the turn again.
        var isSceneSystemLine = message.Type is Guild.Contracts.Bus.Events.MessageType.SceneCharacterJoined
            or Guild.Contracts.Bus.Events.MessageType.SceneCharacterLeft;

        if (channel is { Type: ChannelType.Scene } && !isSceneSystemLine)
        {
            var scene = await scenes.GetAsync(message.ChannelId);
            if (scene is not null)
            {
                // Every message counts, not only the ones that moved the turn: "412 posts over two
                // months" is what a concluded scene has to say about itself.
                scene.PostCount++;

                // Ahead of the advance so the character is in the rotation before the turn moves,
                // and after the message so the join lands under what caused it.
                await joins.AutoJoinOnPostAsync(
                    scene, message.PersonaId, message.AuthorId, message.CreatedAt);

                await scenes.AdvanceOnPostAsync(scene, message.PersonaId, message.CreatedAt);
            }
        }

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
            AuthorIdType = message.AuthorIdType,
            AuthorDisplayName = message.AuthorDisplayName,
            AuthorAvatarUrl = message.AuthorAvatarUrl,
            PersonaId = message.PersonaId,
        });

        // One cache read decides every pair in this fan-out: the author's block state lists both
        // directions, and every question below is "author versus one recipient".
        var blockView = await blocks.GetAsync([message.AuthorId]);

        var mentioned = await ResolveMentionedMembersAsync(
            message, cachedGuildId, presence, viewerIds, context, blockView, personaMentions);

        await RecordBroadcastMentionsAsync(message, context);

        await PublishMentionIndexAsync(message, cachedGuildId, mentioned, bus);

        await PushMentionAddedAsync(message, cachedGuildId, mentioned, hub);

        await PublishPushRecipientsAsync(message, cachedGuildId, presence, mentioned, context, notificationService, bus,
            blockView, privacySettings, audience);
    }

    /// <summary>A mentioned member.</summary>
    private sealed record MentionedMember(string MemberId, string UserId);

    /// <summary>Who a message mentioned, split by how.</summary>
    private sealed record MentionedMembers(
        List<MentionedMember> Direct,
        HashSet<string> ByRole,
        List<MentionedMember> Here,
        List<MentionedMember> ByPersona)
    {
        /// <summary>Everyone individually named by this message - what goes in the index. The order
        /// is the precedence: named outright beats named through a character, which beats being
        /// swept up by an @here.</summary>
        public IEnumerable<MentionedMember> Indexable =>
            Direct.Concat(ByPersona).Concat(Here).DistinctBy(m => m.UserId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves who this message mentioned - direct @user, members holding an @role-mentioned role,
    /// the whole guild (@everyone), the people actually present (@here), or whoever answers for a
    /// character it named.
    /// </summary>
    private static async Task<MentionedMembers> ResolveMentionedMembersAsync(
        MessageCreatedForChannel message,
        string guildId,
        IReadOnlyCollection<MemberPresenceState> presence,
        IReadOnlyCollection<string> viewerIds,
        MicroserviceContext context,
        BlockView blockView,
        PersonaMentionService personaMentions)
    {
        var direct = new List<MentionedMember>();
        var byRole = new HashSet<string>(StringComparer.Ordinal);
        var here = new List<MentionedMember>();
        var byPersona = new List<MentionedMember>();

        bool Reaches(string userId) => !blockView.AreBlocked(message.AuthorId, userId);

        if (message.Mentions.Count > 0)
        {
            direct.AddRange((await context.GuildMembers
                    .AsNoTracking()
                    .Where(m => m.GuildId == guildId
                                && message.Mentions.Contains(m.UserId)
                                && m.UserId != message.AuthorId)
                    .Select(m => new MentionedMember(m.Id, m.UserId))
                    .ToListAsync())
                .Where(m => Reaches(m.UserId)));
        }

        if (message.RoleMentions.Count > 0)
        {
            // The user id is projected alongside the member id purely so the block filter has
            // something to test - everything downstream of byRole keys on the member.
            byRole.UnionWith((await context.RoleMembers
                    .AsNoTracking()
                    .Where(rm => message.RoleMentions.Contains(rm.RoleId)
                                 && rm.Member.UserId != message.AuthorId)
                    .Select(rm => new MentionedMember(rm.MemberId, rm.Member.UserId))
                    .ToListAsync())
                .Where(m => Reaches(m.UserId))
                .Select(m => m.MemberId));
        }

        // Deliberately no @everyone expansion.

        if (message.MentionsHere)
        {
            // Two filters the old code did not apply, both of which produced mentions for people
            // the message never reached:
            var viewers = viewerIds.ToHashSet(StringComparer.Ordinal);

            here.AddRange(presence
                .Where(p => viewers.Contains(p.UserId) && IsOnline(p.Status) && Reaches(p.UserId))
                .Select(p => new MentionedMember(p.MemberId, p.UserId)));
        }

        // A persona mention is read out of the body rather than off a recipient list, so the wire
        // never carries the owner's id and an unpatched consumer cannot resolve one. Ciphertext has
        // no body to read, which is the same exclusion previews and search already make.
        IReadOnlyList<string> personaIds =
            message.EncryptionState == Guild.Contracts.Bus.Events.MessageEncryptionState.Plain
                ? PersonaMentionService.Parse(message.Content)
                : [];

        if (personaIds.Count > 0)
        {
            var reachedUserIds = (await personaMentions.ResolveAsync(guildId, message.AuthorId, personaIds))
                .Select(t => t.UserId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            byPersona.AddRange((await context.GuildMembers
                    .AsNoTracking()
                    .Where(m => m.GuildId == guildId
                                && reachedUserIds.Contains(m.UserId)
                                && m.UserId != message.AuthorId)
                    .Select(m => new MentionedMember(m.Id, m.UserId))
                    .ToListAsync())
                .Where(m => Reaches(m.UserId)));
        }

        return new MentionedMembers(direct, byRole, here, byPersona);
    }

    /// <summary>Whether a presence entry counts as "here".</summary>
    private static bool IsOnline(string? status) =>
        Enum.TryParse<OnlineStatus>(status, ignoreCase: true, out var parsed) && parsed == OnlineStatus.Online;

    /// <summary>
    /// Hands the individually-named recipients to Messaging's mention index, chunked and off the
    /// hot path.
    /// </summary>
    private static async Task PublishMentionIndexAsync(
        MessageCreatedForChannel message, string guildId, MentionedMembers mentioned, IMessageBus bus)
    {
        var recipients = mentioned.Indexable.ToList();
        if (recipients.Count == 0) return;

        var kindOf = KindResolver(mentioned);

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
                    .Select(m => new MentionRecipient { UserId = m.UserId, Kind = kindOf(m.UserId) })
                    .ToList(),
            });
        }
    }

    /// <summary>
    /// Which kind one recipient's index row carries. Direct wins when several apply - being named
    /// outright is the more specific fact, and it is the one the client renders differently.
    /// </summary>
    private static Func<string, string> KindResolver(MentionedMembers mentioned)
    {
        var directUserIds = mentioned.Direct.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        var personaUserIds = mentioned.ByPersona.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);

        return userId => directUserIds.Contains(userId)
            ? nameof(MentionKind.Direct)
            : personaUserIds.Contains(userId)
                ? PersonaMentionService.MentionKindName
                : nameof(MentionKind.Here);
    }

    /// <summary>
    /// Tells the mentioned users' open clients, so a badge lights up without polling.
    /// </summary>
    private static async Task PushMentionAddedAsync(
        MessageCreatedForChannel message,
        string guildId,
        MentionedMembers mentioned,
        IHubContext<EchoRealtimeHub> hub)
    {
        var recipients = mentioned.Indexable.ToList();
        if (recipients.Count == 0) return;

        var kindOf = KindResolver(mentioned);

        // Addressed to the recipients of one kind at a time, so nobody is told who else a character
        // reached.
        foreach (var group in recipients.GroupBy(r => kindOf(r.UserId), StringComparer.Ordinal))
        {
            await hub.Clients.Users(group.Select(r => r.UserId).ToList()).SendAsync("inbox.MentionAdded", new
            {
                MessageId = message.MessageId,
                ChannelId = message.ChannelId,
                GuildId = guildId,
                ConversationId = (string?)null,
                AuthorId = message.AuthorId,
                Kind = group.Key,
                CreatedAt = message.CreatedAt,
            });
        }
    }

    /// <summary>
    /// Works out who should get a phone notification for this message and hands the list to
    /// Messaging, which owns Firebase.
    /// </summary>
    private static async Task PublishPushRecipientsAsync(
        MessageCreatedForChannel message,
        string guildId,
        IReadOnlyCollection<MemberPresenceState> presence,
        MentionedMembers mentioned,
        MicroserviceContext context,
        NotificationResolutionService notificationService,
        IMessageBus bus,
        BlockView blockView,
        PrivacySettingsCache privacySettings,
        ChannelAudienceService audience)
    {
        // Everyone who could conceivably be notified: whoever the message named, plus whoever's
        // settings say "tell me about everything" - that case is precisely what the mention set
        // does not cover.
        var namedMemberIds = mentioned.Direct.Select(m => m.MemberId)
            .Concat(mentioned.ByRole)
            .Concat(mentioned.Here.Select(m => m.MemberId))
            .Concat(mentioned.ByPersona.Select(m => m.MemberId))
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

        // Being called by your character's name is as direct as being called by your own, so a
        // persona mention answers to the same OnlyMentions setting rather than a new one.
        var directMemberIds = mentioned.Direct.Concat(mentioned.ByPersona)
            .Select(m => m.MemberId)
            .ToHashSet(StringComparer.Ordinal);

        var hereMemberIds = mentioned.Here.Select(m => m.MemberId).ToHashSet(StringComparer.Ordinal);

        var recipients = new List<string>();
        foreach (var candidate in candidates)
        {
            if (connectedUserIds.Contains(candidate.UserId)) continue;
            if (!resolved.TryGetValue(candidate.MemberId, out var settings)) continue;
            if (!settings.MobilePush) continue;

            // The mention sets were already filtered, but the candidate set is wider than they are
            // - at the AllMessages default it is the whole membership - so the blocker would still
            // have been pushed an ordinary message from the person they blocked.
            if (blockView.AreBlocked(message.AuthorId, candidate.UserId)) continue;

            var isDirectMention = directMemberIds.Contains(candidate.MemberId);
            var isRoleMention = mentioned.ByRole.Contains(candidate.MemberId);

            // @here resolves against the presence snapshot taken when this message was handled, so
            // it only counts for members who were actually here. @everyone counts for everyone.
            var isEveryoneMention = message.MentionsEveryone || hereMemberIds.Contains(candidate.MemberId);

            if (settings.ShouldNotify(isDirectMention, isRoleMention, isEveryoneMention))
                recipients.Add(candidate.UserId);
        }

        if (recipients.Count == 0) return;

        // The candidate set is guild-wide - at the AllMessages default it is the whole membership -
        // but the push carries the message body, so it has to answer to ViewChannel exactly as the
        // realtime broadcast above does. Last, on the set the cheap filters have already cut down,
        // and against the same 10-second decision cache the broadcast just populated.
        recipients = await audience.FilterToViewersAsync(message.ChannelId, recipients);

        if (recipients.Count == 0) return;

        // Privacy spec T2-23. A recipient with HidePushContent gets routing ids and nothing else -
        // no body, no author name, no channel name.
        var recipientSettings = await privacySettings.GetAsync(recipients);

        var hidden = recipients
            .Where(id => recipientSettings.TryGetValue(id, out var s) && s.HidePushContent)
            .ToList();

        var plain = recipients.Except(hidden, StringComparer.Ordinal).ToList();

        var isEncrypted = message.EncryptionState != Guild.Contracts.Bus.Events.MessageEncryptionState.Plain;

        if (plain.Count > 0)
        {
            await bus.PublishAsync(new ChannelPushRequested
            {
                GuildId = guildId,
                ChannelId = message.ChannelId,
                MessageId = message.MessageId,
                UserIds = plain,
                AuthorId = message.AuthorId,
                Content = message.Content,
                IsEncrypted = isEncrypted,
                MlsGeneration = message.MlsGeneration,
            });
        }

        if (hidden.Count > 0)
        {
            await bus.PublishAsync(new ChannelPushRequested
            {
                GuildId = guildId,
                ChannelId = message.ChannelId,
                MessageId = message.MessageId,
                UserIds = hidden,
                // Blank, not the real id: Messaging resolves the author's display name from it for
                // the notification title, and the author's name is exactly what T2-23 forbids.
                AuthorId = string.Empty,
                Content = [],
                IsEncrypted = isEncrypted,
                // Withheld with the body: it is only useful for decrypting ciphertext that is not
                // being sent.
                MlsGeneration = null,
                HideContent = true,
            });
        }
    }

    /// <summary>
    /// Denormalizes the head of the channel onto its row: when it last saw activity, which message
    /// was last, and how many there have been. Returns the channel so the caller does not read it
    /// twice.
    /// </summary>
    private static async Task<Guild.Domain.Aggregates.Channel?> TouchChannelActivityAsync(
        string channelId, DateTimeOffset messageCreatedAt, string messageId, MicroserviceContext context)
    {
        var channel = await context.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return null;

        channel.LastActivityAt = messageCreatedAt;
        channel.LastMessageId = messageId;
        channel.MessageCount++;

        // Slides against the stored window, not against the previous deadline - the duration is
        // fixed for the post's lifetime, only the deadline moves.
        if (channel.Type.IsThreadShaped() && channel.AutoArchiveMinutes is > 0)
            channel.AutoArchiveAt = messageCreatedAt.AddMinutes(channel.AutoArchiveMinutes.Value);

        return channel;
    }

    /// <summary>
    /// Records `@everyone`/`@here`/`@role` as one row per ping rather than one per recipient.
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