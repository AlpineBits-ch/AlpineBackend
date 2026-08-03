using Echo.Realtime;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Bus.Events.Realtime;

public class GuildReadHandler
{
    public async Task Handle(
        UpdateGuildReadCommand message,
        MicroserviceContext microserviceContext,
        IMessageBus bus,
        ILogger<GuildReadHandler> logger)
    {
        var channel = await microserviceContext.Channels.Select(c => new
        {
            c.Id,
            c.GuildId,
            c.LastMessageId,
            c.LastActivityAt,
            c.MessageCount,
        }).FirstOrDefaultAsync(c => c.Id == message.ChannelId);
        if (channel is null)
        {
            logger.LogWarning("Channel with ID {ChannelId} not found in context", message.ChannelId);
            return;
        }

        var member = await microserviceContext.GuildMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == message.UserId && m.GuildId == channel.GuildId);
        if (member is null)
        {
            logger.LogWarning("member with not found");
            return;
        }

        var lastRead =
            await microserviceContext.ReadStates.FirstOrDefaultAsync(r =>
                r.ChannelId == message.ChannelId && r.MemberId == member.Id);
        if (lastRead is null)
        {
            lastRead = new ReadState
            {
                Id = ReadState.GenerateId(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ChannelId = message.ChannelId,
                MemberId = member.Id
            };
            await microserviceContext.ReadStates.AddAsync(lastRead);
        }

        lastRead.LastReadMessageId = message.Id;
        lastRead.LastReadAt = await ResolveReadTimeAsync(message.Id, channel.LastMessageId, channel.LastActivityAt, bus, logger);
        lastRead.MessageCountAtRead = channel.MessageCount;
        lastRead.UpdatedAt = DateTime.UtcNow;
        await microserviceContext.SaveChangesAsync();
    }

    /// <summary>
    /// The stored CreatedAt of the acked message - what the unread predicate compares against.
    ///
    /// Almost always free: a client acks what it just received, so the acked id is the channel's
    /// head and the answer is the LastActivityAt already denormalized onto the channel row. The
    /// round trip is only paid when someone acks while scrolled up.
    ///
    /// Falling back to now on failure over-marks rather than under-marks - the member loses an
    /// unread badge they might have wanted, instead of a channel that will not stop shouting at
    /// them. Neither is good, and it is logged, but the second is the one people notice.
    /// </summary>
    private static async Task<DateTimeOffset?> ResolveReadTimeAsync(
        string? ackedMessageId,
        string? channelLastMessageId,
        DateTimeOffset? channelLastActivityAt,
        IMessageBus bus,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(ackedMessageId)) return channelLastActivityAt ?? DateTimeOffset.UtcNow;

        if (channelLastMessageId is not null
            && string.Equals(ackedMessageId, channelLastMessageId, StringComparison.Ordinal)
            && channelLastActivityAt is not null)
        {
            return channelLastActivityAt;
        }

        try
        {
            var response = await bus.InvokeAsync<GetMessageResponse>(new GetMessageRequest { MessageId = ackedMessageId });
            if (response.Message is not null) return response.Message.CreatedAt;

            // Acking a message that no longer exists is ordinary - it was deleted between being
            // rendered and being acked. The channel head is the honest answer.
            return channelLastActivityAt ?? DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not resolve the timestamp of acked message {MessageId}; falling back to now",
                ackedMessageId);
            return DateTimeOffset.UtcNow;
        }
    }
}
