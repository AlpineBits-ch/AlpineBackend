using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;

namespace Messaging.Application.Handler.Messages;

/// <summary>Serves the mention-index reads behind the inbox's Mentions tab.</summary>
public class GetUserMentionsHandler
{
    public static async Task<GetUserMentionsResponse> Handle(
        GetUserMentionsRequest request, IMentionIndexRepository repo)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return new GetUserMentionsResponse { Mentions = [] };

        var rows = await repo.GetPageAsync(new MentionPageQuery
        {
            UserId = request.UserId,
            Before = request.Before,
            BeforeMessageId = request.BeforeMessageId,
            Since = request.Since,
            // Over-fetch so the filters below cannot hand back a short page while more rows exist.
            Limit = request.GuildId is not null || !request.IncludeDms ? request.Limit * 3 : request.Limit,
        });

        var filtered = rows
            .Where(m => request.GuildId is null || m.GuildId == request.GuildId)
            .Where(m => request.IncludeDms || m.GuildId is not null)
            .Take(request.Limit)
            .Select(Project)
            .ToList();

        return new GetUserMentionsResponse { Mentions = filtered };
    }

    public static async Task<DeleteUserMentionResponse> Handle(
        DeleteUserMentionRequest request, IMentionIndexRepository repo)
    {
        await repo.DeleteAsync(request.UserId, request.CreatedAt, request.MessageId);

        // Reported as deleted whether or not a row was there.
        return new DeleteUserMentionResponse { Deleted = true };
    }

    private static UserMentionEntry Project(UserMention mention) => new()
    {
        UserId = mention.UserId,
        CreatedAt = mention.CreatedAt,
        MessageId = mention.MessageId,
        ContextId = mention.ContextId,
        GuildId = mention.GuildId,
        ChannelId = mention.ChannelId,
        ConversationId = mention.ConversationId,
        AuthorId = mention.AuthorId,
        Kind = mention.Kind,
    };
}
