using Federation.Application.Providers;
using Federation.Application.Services;
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
/// piggybacks on the existing contracts rather than requiring new publish points). A channel's
/// federation status is resolved indirectly through its owning guild: Federation doesn't have
/// its own copy of the channel->guild mapping, so it asks Guild.Application for it via the same
/// GetChannelRequest/Response Wolverine request-response Guild already exposes.
///
/// AuthorId/UserId already in federated (&lt;id&gt;:&lt;domain&gt;) form means this event
/// originated from Messaging's own materialization of an inbound federation event (see
/// MessagingMaterializationHandlers) - skip it, or it would echo straight back out to the
/// instance it just arrived from.
/// </summary>
public class MessagingOutboundHandlers
{
    public static Task Handle(MessageCreatedForChannel message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.AuthorId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.SendMessageAsync(federatedChannelId, message.MessageId, message.Content, userService.GetFederatedUserId(message.AuthorId), ct));

    public static Task Handle(MessageUpdatedForChannel message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.AuthorId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.EditMessageAsync(federatedChannelId, message.MessageId, message.Content, userService.GetFederatedUserId(message.AuthorId), ct));

    public static Task Handle(MessageDeletedForChannel message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.AuthorId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.DeleteMessageAsync(federatedChannelId, message.MessageId, userService.GetFederatedUserId(message.AuthorId), ct));

    public static Task Handle(ReactionCreatedEvent message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.UserId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.AddReactionAsync(federatedChannelId, message.MessageId, message.Emoji, userService.GetFederatedUserId(message.UserId), ct));

    public static Task Handle(ReactionRemovedEvent message, IFederationProvider provider, UserService userService, MicroserviceContext db, IMessageBus bus, CancellationToken ct) =>
        IsFederated(message.UserId) ? Task.CompletedTask : ForEachLinkedInstanceAsync(message.ChannelId, db, bus, ct,
            federatedChannelId => provider.RemoveReactionAsync(federatedChannelId, message.MessageId, message.Emoji, userService.GetFederatedUserId(message.UserId), ct));

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
