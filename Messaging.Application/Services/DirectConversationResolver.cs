using Messaging.Application.Services.Privacy;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>Why a direct conversation could not be produced.</summary>
public enum DirectConversationOutcome
{
    Found,
    Created,

    /// <summary>The recipient's own <c>DirectMessagePolicy</c> - or a block in either direction -
    /// refuses a first contact from this sender. Nothing is created.</summary>
    Refused,

    /// <summary>One of the two accounts could not be resolved, so a conversation could not be
    /// stamped with the cached names every member row carries.</summary>
    ProfileUnavailable,

    SameUser,
}

public readonly record struct DirectConversationResult(DirectConversationOutcome Outcome, string? ConversationId)
{
    public bool HasConversation => ConversationId is not null;
}

/// <summary>
/// Finds the 1:1 conversation between two people, and starts one if there is none.
/// </summary>
public sealed class DirectConversationResolver(
    MicroserviceContext db,
    DirectMessagePolicyService dmPolicy,
    IMessageBus bus,
    ILogger<DirectConversationResolver> logger)
{
    /// <summary>
    /// The conversation <paramref name="initiatorUserId"/> should write to in order to reach
    /// <paramref name="recipientUserId"/>.
    /// </summary>
    public async Task<DirectConversationResult> ResolveAsync(
        string initiatorUserId, string recipientUserId, CancellationToken ct = default)
    {
        if (string.Equals(initiatorUserId, recipientUserId, StringComparison.Ordinal))
            return new DirectConversationResult(DirectConversationOutcome.SameUser, null);

        var existing = await db.Conversations
            .AsNoTracking()
            .Where(c => c.OriginInstanceId == null
                        && c.Members.Count == 2
                        && c.Members.Any(m => m.UserId == initiatorUserId)
                        && c.Members.Any(m => m.UserId == recipientUserId))
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.CreatedAt)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
            return new DirectConversationResult(DirectConversationOutcome.Found, existing);

        var initiator = await ProfileAsync(initiatorUserId, ct);
        var recipient = await ProfileAsync(recipientUserId, ct);

        // Not a degraded-but-usable state, unlike a nameless push notification: CachedUserName and
        // CachedUserHash are how every conversation list renders the other party, and a row written
        // with placeholders would be wrong in the database rather than wrong on one screen.
        if (initiator is null || recipient is null)
            return new DirectConversationResult(DirectConversationOutcome.ProfileUnavailable, null);

        var refusal = await dmPolicy.EvaluateAsync(
            initiatorUserId,
            [recipientUserId],
            new Dictionary<string, ProfileDto>(StringComparer.Ordinal) { [recipientUserId] = recipient },
            ct);

        if (refusal is not null)
        {
            logger.LogDebug(
                "Not starting a direct conversation between {InitiatorId} and {RecipientId}: {Code}",
                initiatorUserId, recipientUserId, refusal.Code);
            return new DirectConversationResult(DirectConversationOutcome.Refused, null);
        }

        // Plain, always.
        var conversation = Conversation.Create(new CreateConversationParams
        {
            Encryption = ChannelEncryptionState.Plain,
            Members =
            [
                Member(initiatorUserId, initiator),
                Member(recipientUserId, recipient),
            ],
        });

        db.Conversations.Add(conversation);

        // Committed here rather than left to the transactional middleware at the end of the calling
        // handler, for the same reason the guild-join path does it: whatever the caller writes next
        // names this conversation, and a message write that runs in its own scope, on its own
        // connection, cannot see a row that has not been committed.
        await db.SaveChangesAsync(ct);

        return new DirectConversationResult(DirectConversationOutcome.Created, conversation.Id);
    }

    private static CreateConversationMemberParams Member(string userId, ProfileDto profile) => new()
    {
        UserId = userId,
        PublicKey = [],
        CachedUserName = profile.UserName ?? "",
        CachedUserHash = profile.Hash,
    };

    private async Task<ProfileDto?> ProfileAsync(string userId, CancellationToken ct)
    {
        try
        {
            var response = await bus.InvokeAsync<GetProfileByUserIdResponse>(
                new GetProfileByUserIdRequest { UserId = userId }, ct);
            return response.Profile;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not resolve the profile of {UserId} while opening a direct conversation", userId);
            return null;
        }
    }
}
