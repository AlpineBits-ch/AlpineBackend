using Federation.Contracts.Materialization.Conversation;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Bus.Federation;

/// <summary>
/// Materializes remote DM-conversation federation events as shadow Conversation/ConversationMember
/// rows, flagged via Conversation.OriginInstanceId (Phase 1) and the existing (previously unused)
/// ConversationMember.FederatedServerId field respectively.
///
/// No re-publish of local domain events here (unlike MessagingMaterializationHandlers): DM
/// conversation create/member-add/member-remove don't currently drive any further downstream fan
/// -out beyond what a client polls for, so there's nothing to reuse and no echo risk to guard
/// against.
///
/// Idempotent by natural business key (ConversationId, ConversationId+UserId), not EventId - same
/// reasoning as the other materialization handlers.
/// </summary>
public class ConversationMaterializationHandlers
{
    public static async Task Handle(FederatedConversationCreatedReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var exists = await db.Conversations.AnyAsync(c => c.Id == message.ConversationId, ct);
        if (exists) return;

        db.Conversations.Add(new Conversation
        {
            Id = message.ConversationId,
            OriginInstanceId = message.OriginInstanceId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            // PublicKey/CachedUserName are IsRequired() (NOT NULL) columns - unlike
            // ConversationEndpoints.CreateConversation, there's no cross-service profile lookup
            // available here to populate a real cached display name, so we default to the
            // federated user id (same empty-key convention as a non-MLS local member) rather than
            // leaving them null, which would fail this SaveChangesAsync with a constraint
            // violation.
            Members = message.MemberIds.Select(userId => ConversationMember.Create(new CreateConversationMemberParams
            {
                ConversationId = message.ConversationId,
                UserId = userId,
                PublicKey = Array.Empty<byte>(),
                CachedUserName = userId,
            })).ToList(),
        });
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(FederatedConversationMemberAddedReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var exists = await db.Members.AnyAsync(
            m => m.ConversationId == message.ConversationId && m.UserId == message.UserId, ct);
        if (exists) return;

        // See the matching comment in the FederatedConversationCreatedReceived handler above:
        // PublicKey/CachedUserName are NOT NULL columns with no profile data available here.
        var member = ConversationMember.Create(new CreateConversationMemberParams
        {
            ConversationId = message.ConversationId,
            UserId = message.UserId,
            PublicKey = Array.Empty<byte>(),
            CachedUserName = message.UserId,
        });
        member.FederatedServerId = message.OriginInstanceId;

        db.Members.Add(member);
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(FederatedConversationMemberLeftReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var member = await db.Members.FirstOrDefaultAsync(
            m => m.ConversationId == message.ConversationId && m.UserId == message.UserId, ct);
        if (member is null) return;

        db.Members.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(FederatedConversationDeletedReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var conversation = await db.Conversations
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);
        if (conversation is null) return;

        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(ct);
    }

    // No handler for FederatedConversationEditedReceived: Messaging.Application has no
    // edit/rename endpoint for conversations at all yet (see ConversationOutboundHandlers'
    // header comment) - nothing to apply the edit to.
}
