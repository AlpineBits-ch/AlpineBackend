using System.Text;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Events.Call;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;
using Wolverine.Attributes;

namespace Messaging.Application.Handler.Call;

/// <summary>Reads through the DbContext but writes nothing with it - the history entry goes through
/// CreateMessageCommand, which owns its own transaction. Marked so this handler does not open one
/// for a membership lookup, same as the other fan-out handlers in this service.</summary>
[NonTransactional]
public class CallEndedHandler
{
    /// <summary>Everything that has to stop being true once a call is over.</summary>
    public static async Task Handle(
        CallEnded @event,
        IDistributedCache cache,
        StreamViewerStore viewers,
        IHubContext<EchoRealtimeHub> hub,
        MicroserviceContext db,
        IMessageBus bus)
    {
        // Nobody can be watching a share in a call that is over.
        await viewers.DropAsync(StreamViewerStore.CallScope(@event.CallId));

        if (!string.IsNullOrWhiteSpace(@event.ConversationId))
        {
            await cache.RemoveAsync(CallService.ConversationCallKey(@event.ConversationId));

            var memberIds = await db.Members
                .AsNoTracking()
                .Where(m => m.ConversationId == @event.ConversationId)
                .Select(m => m.UserId)
                .ToListAsync();

            // Addressed to the whole conversation, not just the call's roster: the "join the call
            // in progress" affordance is shown to members who were never in it, so they are exactly
            // the people who need telling it is gone.
            if (memberIds.Count > 0)
            {
                await hub.Clients.Users(memberIds).SendAsync("conversation.CallStateChanged", new
                {
                    conversationId = @event.ConversationId,
                    callId = @event.CallId,
                    status = "Ended",
                    reason = @event.Reason.ToString(),
                    participantIds = Array.Empty<string>(),
                });
            }

            await WriteHistoryEntryAsync(@event, bus);
        }

        await cache.RemoveAsync(Domain.Entities.Call.GetCacheId(@event.CallId));
    }

    /// <summary>
    /// Leaves the call in the conversation's history, the way Discord does: one entry, written when
    /// the call is over, saying either how long it ran or that it was never answered.
    /// </summary>
    private static async Task WriteHistoryEntryAsync(CallEnded @event, IMessageBus bus)
    {
        if (string.IsNullOrWhiteSpace(@event.CreatorId)) return;

        var missed = !@event.Answered;

        // Whole seconds, floored, never negative - a clock that went backwards between the two
        // reads must not produce a call that ran for minus four seconds.
        var elapsed = DateTimeOffset.UtcNow - @event.StartedAt;
        var seconds = elapsed < TimeSpan.Zero ? 0 : (long)elapsed.TotalSeconds;

        await bus.InvokeAsync(new CreateMessageCommand
        {
            ConversationId = @event.ConversationId,
            AuthorId = @event.CreatorId,
            AuthorIdType = AuthorIdType.User,
            Type = missed ? MessageType.CallMissed : MessageType.CallEnded,
            Content = missed ? [] : Encoding.UTF8.GetBytes(seconds.ToString()),
            Mentions = [],
        });
    }
}
