using System.Text.Json;
using AppEnvironment;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Application.Handler.Account;

/// <summary>
/// Messaging's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling
/// of <see cref="PurgeUserDataCommandHandler"/>.
/// </summary>
public class ExportUserDataCommandHandler
{
    /// <summary>Rows read per page while walking a conversation.</summary>
    private const int PageSize = 200;

    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command,
        MicroserviceContext ctx,
        IMessageRepository messages,
        ILogger<ExportUserDataCommandHandler> logger)
    {
        var memberships = await ctx.Members
            .AsNoTracking()
            .Where(m => m.UserId == command.UserId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var conversationIds = memberships.Select(m => m.ConversationId).Distinct().ToList();

        var conversations = await ctx.Conversations
            .AsNoTracking()
            .Where(c => conversationIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.EncryptionState, c.CreatedAt })
            .ToListAsync();

        var conversationById = conversations.ToDictionary(c => c.Id);

        // A point-in-time snapshot: anything sent after the export was requested is simply not in it.
        var asOf = DateTimeOffset.UtcNow;
        var cap = Env.DataExport.MaxMessagesPerConversation;

        // Keyed by channel as well as by conversation, so unlike everything else here it is not
        // reachable from the membership list.
        var drafts = await ctx.MessageDrafts
            .AsNoTracking()
            .Where(d => d.UserId == command.UserId)
            .OrderBy(d => d.UpdatedAt)
            .Select(d => new { d.ContextId, d.ChannelId, d.ConversationId, d.Content, d.InReplyTo, d.UpdatedAt })
            .ToListAsync();

        var exportedConversations = new List<object>();
        var totalMessages = 0;

        foreach (var conversationId in conversationIds)
        {
            var (own, truncated) = await ReadOwnMessagesAsync(
                messages, conversationId, command.UserId, asOf, cap);

            totalMessages += own.Count;

            conversationById.TryGetValue(conversationId, out var conversation);

            exportedConversations.Add(new
            {
                conversationId,
                name = conversation?.Name,
                encryptionState = conversation?.EncryptionState.ToString(),
                createdAt = conversation?.CreatedAt,
                truncated,
                messages = own.Select(m => new
                {
                    m.Id,
                    m.CreatedAt,
                    encryptionState = m.EncryptionState.ToString(),
                    type = m.Type.ToString(),
                    m.InReplyTo,
                    m.SenderDeviceId,
                    // Base64 whether it is ciphertext or plaintext bytes, so one decoding rule
                    // applies to the whole archive.
                    contentBase64 = m.Content is null ? null : Convert.ToBase64String(m.Content),
                }).ToList(),
            });
        }

        var fragment = new
        {
            notice =
                "Messages in end-to-end encrypted conversations are stored as ciphertext this server "
                + "cannot read, and are exported as stored. Only messages you sent are included: the "
                + "other participants' messages are their personal data, not yours.",
            asOf,
            conversations = exportedConversations,
            memberships = memberships.Select(m => new
            {
                m.Id,
                m.ConversationId,
                m.CachedUserName,
                m.LastReadMessageId,
                m.MentionCount,
                m.MutedUntil,
                m.FederatedServerId,
                m.CreatedAt,
                // No other member of the conversation appears here - see this handler's remarks.
            }),
            drafts,
        };

        logger.LogInformation(
            "Data export {ExportId}: {Messages} message(s) authored by {UserId} across {Conversations} conversation(s)",
            command.ExportId, totalMessages, command.UserId, conversationIds.Count);

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "messaging",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>
            {
                ["conversations"] = exportedConversations.Count,
                ["memberships"] = memberships.Count,
                ["messages"] = totalMessages,
                ["drafts"] = drafts.Count,
            },
        };
    }

    /// <summary>
    /// Walks one conversation's history forwards, keeping only the rows this account authored.
    /// </summary>
    private static async Task<(List<Message> Messages, bool Truncated)> ReadOwnMessagesAsync(
        IMessageRepository repository, string conversationId, string userId, DateTimeOffset asOf, int cap)
    {
        var collected = new List<Message>();

        var afterCreatedAt = DateTimeOffset.MinValue;
        var afterMessageId = string.Empty;

        while (true)
        {
            // Already materialized by the repository.
            var page = await repository.GetContextMessagesOlderThanAsync(
                conversationId, asOf, afterCreatedAt, afterMessageId, PageSize);

            if (page.Count == 0) return (collected, false);

            var last = page[^1];
            afterCreatedAt = last.CreatedAt;
            afterMessageId = last.Id;

            foreach (var message in page)
            {
                if (!string.Equals(message.AuthorId, userId, StringComparison.Ordinal)) continue;

                collected.Add(message);

                // Reported, not hidden. An archive that quietly stops at N looks complete and is not.
                if (collected.Count >= cap) return (collected, true);
            }

            if (page.Count < PageSize) return (collected, false);
        }
    }
}
