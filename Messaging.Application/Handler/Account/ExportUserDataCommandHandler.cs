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
/// Messaging's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
///
/// <para><b>Only the messages the subject sent.</b> Scoped by <c>AuthorId</c> in every conversation
/// they are a member of - the same author filter <c>DmRetentionSweepService</c> uses, and the same
/// reasoning: the other side of a conversation is somebody else's writing, in somebody else's copy of
/// a history they were also part of. Handing a subject a transcript of what other people said to them
/// would put those people's personal data - what they said, when, from which device - into a file the
/// subject can do anything with, and would do it without ever asking them. The spec is explicit:
/// message bodies the user sent, yes; other people's, no.</para>
///
/// <para><b>Conversation membership is included, other members are not.</b> The subject sees which
/// conversations they are in, their own read position, their own mute settings and their own cached
/// display name. The other members appear nowhere - not even as ids, because a group DM's membership
/// is a fact about the whole group.</para>
///
/// <para><b>Encrypted content is exported as it is stored, which is ciphertext.</b> Messages in an
/// MLS conversation are end-to-end encrypted; this service has never been able to read them and
/// cannot decrypt them for an export. The bytes are base64'd, the encryption state is stated per
/// message, and the fragment says so at the top rather than leaving a subject to conclude their
/// archive is corrupt. Plaintext-mode messages come out readable.</para>
///
/// <para><b>Scylla's RowSet is single-pass.</b> Every page returned by the repository is already
/// materialized by it (see <c>ScyllaMessageRepository</c>), and nothing here enumerates a returned
/// sequence twice. The per-conversation cap comes from <c>Env.DataExport.MaxMessagesPerConversation</c>
/// and, when it bites, is reported as <c>truncated: true</c> on that conversation rather than
/// silently trimming the archive.</para>
/// </summary>
public class ExportUserDataCommandHandler
{
    /// <summary>Rows read per page while walking a conversation. Pages are read and filtered by
    /// author here, not in the store - <c>author_id</c> is not part of the messages primary key, so
    /// restricting on it in CQL would need ALLOW FILTERING and a partition scan.</summary>
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
            },
        };
    }

    /// <summary>
    /// Walks one conversation's history forwards, keeping only the rows this account authored.
    ///
    /// <para>The cursor advances off the last row of each <i>page</i>, not off the last row the
    /// subject wrote. Advancing off the subject's own rows would stall the scan forever behind a run
    /// of somebody else's messages - the same trap <c>DmRetentionSweepService</c> documents.</para>
    /// </summary>
    private static async Task<(List<Message> Messages, bool Truncated)> ReadOwnMessagesAsync(
        IMessageRepository repository, string conversationId, string userId, DateTimeOffset asOf, int cap)
    {
        var collected = new List<Message>();

        var afterCreatedAt = DateTimeOffset.MinValue;
        var afterMessageId = string.Empty;

        while (true)
        {
            // Already materialized by the repository. LIMIT 0 is a hard error in CQL, which is why
            // PageSize is a constant and never a computed remainder.
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
