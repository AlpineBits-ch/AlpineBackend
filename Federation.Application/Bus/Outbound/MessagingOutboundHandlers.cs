using Federation.Application.Providers;
using Federation.Application.Services;
using Federation.Contracts.Materialization.Messaging;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Wolverine;

namespace Federation.Application.Bus.Outbound;

/// <summary>
/// Subscribes to the same cross-service message events Guild.Application already consumes
/// (Messaging.Application publishes them for Guild's realtime/bots fan-out - Federation just
/// piggybacks on the existing contracts rather than requiring new publish points).
/// </summary>
public class MessagingOutboundHandler
{
    public static Task Handle(MessageCreatedForChannel message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.AuthorId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.SendMessageAsync(federatedChannelId, message.MessageId, message.Content, userService.GetFederatedUserId(message.AuthorId),
                DisplayOf(message.AuthorDisplayName, message.AuthorAvatarUrl, message.AuthorIdType), ct));

    public static Task Handle(MessageUpdatedForChannel message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.AuthorId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.EditMessageAsync(federatedChannelId, message.MessageId, message.Content, userService.GetFederatedUserId(message.AuthorId),
                DisplayOf(message.AuthorDisplayName, message.AuthorAvatarUrl, message.AuthorIdType), ct));

    public static Task Handle(MessageDeletedForChannel message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.AuthorId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.DeleteMessageAsync(federatedChannelId, message.MessageId, userService.GetFederatedUserId(message.AuthorId), ct));

    public static Task Handle(ReactionCreatedEvent message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.UserId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.AddReactionAsync(federatedChannelId, message.MessageId, message.Emoji, userService.GetFederatedUserId(message.UserId), ct));

    public static Task Handle(ReactionRemovedEvent message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.UserId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.RemoveReactionAsync(federatedChannelId, message.MessageId, message.Emoji, userService.GetFederatedUserId(message.UserId), ct));

    // The persona id itself deliberately does not travel - a federated persona is display data, so
    // only the name, avatar and author type cross the boundary (docs/specs/roleplay-guilds.md 11.2).
    private static FederatedAuthorDisplay DisplayOf(string? displayName, string? avatarUrl, AuthorIdType authorIdType) =>
        displayName is null && avatarUrl is null && authorIdType == AuthorIdType.User
            ? FederatedAuthorDisplay.RealUser
            : new FederatedAuthorDisplay(displayName, avatarUrl, MapAuthorIdType(authorIdType));

    private static FederatedAuthorIdType MapAuthorIdType(AuthorIdType authorIdType) => authorIdType switch
    {
        AuthorIdType.Bot => FederatedAuthorIdType.Bot,
        AuthorIdType.Webhook => FederatedAuthorIdType.Webhook,
        AuthorIdType.Persona => FederatedAuthorIdType.Persona,
        _ => FederatedAuthorIdType.User,
    };

    // A foreign id is qualified as <localId>:<domain>, so a colon means the message arrived from
    // another instance and must not be sent back out.
    private static bool IsFederated(string id) => id.Contains(':');

    private static async Task ForEachLinkedInstanceAsync(
        string channelId, MicroserviceContext db, IMessageBus bus, CancellationToken ct, Func<string, Task> sendAsync)
    {
        var channel = await bus.InvokeAsync<GetChannelResponse>(new GetChannelRequest { ChannelId = channelId }, ct);
        if (channel.Channel is null) return;

        var instances = await FederatedResourceLookup.GetActiveInstancesAsync(
            db, FederatedResourceType.Guild, channel.Channel.GuildId, ct);

        foreach (var instance in instances)
            await sendAsync($"{channelId}:{instance.Host}");
    }
}
