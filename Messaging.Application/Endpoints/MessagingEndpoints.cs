using System.Security.Claims;
using System.Text;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Commands;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;
using Messaging.Domain.Repositories;
// Messaging.Domain.Enums is aliased rather than imported: it defines AuthorIdType and
// MessageEncryptionState under the same names as Messaging.Contracts.Bus.Commands, which this file
// also uses, and importing both makes every existing reference ambiguous.
using MessageFlags = Messaging.Domain.Enums.MessageFlags;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

[Authorize]

public class MessagingEndpoints
{
    /// <summary>Deliberately returns a bare IResult and no cascaded event.</summary>
    [WolverinePost("/api/v1/messaging")]
    public async Task<IResult> CreateMessage(CreateMessageDto dto,  [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext context, [NotBody] IMessageBus bus, [NotBody] IDistributedCache cache, [NotBody] MlsGroupService mls,
        [NotBody] DirectMessagePolicyService dmPolicy, [NotBody] ExplicitContentGuard contentGuard)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId is null) return Results.Unauthorized();

        var authorIdType = user.FindFirstValue("user_type") == "Bot" ? AuthorIdType.Bot : AuthorIdType.User;

        // Both arrays are raw client input and drive per-recipient work downstream (mention
        // indexing, unread counts, push resolution). @everyone is permission-gated below, but role
        // mentions are not - without a cap any member could list every role in the guild and buy an
        // unbounded fan-out with one request. Same limit Discord documents on allowed_mentions.
        var mentions = Truncate(dto.Mentions, MaxMentionsPerMessage);
        var roleMentions = Truncate(dto.RoleMentions, MaxMentionsPerMessage);

        if(string.IsNullOrWhiteSpace(dto.ConversationId) && string.IsNullOrWhiteSpace(dto.ChannelId)) return Results.BadRequest();

        // The two ids are mutually exclusive, and that has to be enforced rather than assumed.
        if (!string.IsNullOrWhiteSpace(dto.ConversationId) && !string.IsNullOrWhiteSpace(dto.ChannelId))
            return Results.BadRequest("Specify either channelId or conversationId, not both.");

        // Authoritative copies of the client's mention flags.
        var mentionsEveryone = dto.MentionsEveryone;
        var mentionsHere = dto.MentionsHere;

        if (!string.IsNullOrWhiteSpace(dto.ChannelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = dto.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.SendMessages
                });

            if(!response.IsAllowed) return Results.Forbid();

            // @everyone/@here is a permission, not a client decision.
            if (mentionsEveryone || mentionsHere)
            {
                var mentionResponse = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                    new HasUserPermissionToChannelRequest()
                    {
                        ChannelId = dto.ChannelId,
                        UserId = userId,
                        Permission = ExternalPermission.MentionEveryone
                    });

                if (!mentionResponse.IsAllowed)
                {
                    mentionsEveryone = false;
                    mentionsHere = false;
                }
            }

            // Bots/webhooks intentionally bypass auto-mod - a guild that installs a bot has
            // already made an explicit trust decision about what it posts.
            if (authorIdType != AuthorIdType.Bot)
            {
                var blockedReason = await AutoModeration.CheckAsync(dto.ChannelId, userId, dto.Content, cache, bus);
                if (blockedReason is not null)
                {
                    await bus.PublishAsync(new Guild.Contracts.Bus.Events.AutoModTriggeredEvent
                    {
                        ChannelId = dto.ChannelId,
                        UserId = userId,
                        Reason = blockedReason,
                    });

                    return Results.Json(new { error = "automod_blocked", reason = blockedReason }, statusCode: StatusCodes.Status403Forbidden);
                }

                // Deliberately the last gate before the message is created: passing the check
                // consumes the author's slowmode window, so anything that can still reject the send
                // (permissions, auto-mod) has to have run first, or a message blocked for an
                // unrelated reason would silently start the cooldown anyway.
                if (response.SlowModeSeconds > 0 && !response.CanBypassSlowMode)
                {
                    var retryAfter = await SlowModeGuard.CheckAsync(dto.ChannelId, userId, response.SlowModeSeconds, cache);
                    if (retryAfter is not null)
                    {
                        return Results.Json(
                            new { error = "slowmode", retry_after = retryAfter.Value, global = false },
                            statusCode: StatusCodes.Status429TooManyRequests);
                    }
                }
            }
        }
        else
        {
            var conversation = await context.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == dto.ConversationId);
            if(conversation is null) return Results.NotFound();

            if(conversation.Members.All(m => m.UserId != userId))
            {
                return Results.Forbid();
            }

            var otherMemberIds = conversation.Members
                .Select(m => m.UserId)
                .Where(u => !string.Equals(u, userId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // A two-person DM is the "new one-to-one send" T0-2 names: the recipient's policy is
            // re-checked on every send, so a person who switches to friends-only (or blocks the
            // sender) stops receiving messages immediately rather than only being protected from
            // conversations that do not exist yet.
            if (otherMemberIds.Count == 1)
            {
                var refusal = await dmPolicy.EvaluateAsync(userId, otherMemberIds);
                if (refusal is not null) return DmRefusalResults.ToResult(refusal);
            }

            // T2-20. Attachment content is filtered against each recipient's ExplicitContentFilter.
            if (dto.Attachments.Count > 0)
            {
                var candidates = await context.Attachments.AsNoTracking()
                    .Where(a => dto.Attachments.Contains(a.Id))
                    .Select(a => new { a.Id, a.FileName, a.ContentType })
                    .ToListAsync();

                var refuseForContent = await contentGuard.ShouldRefuseAsync(
                    userId,
                    otherMemberIds,
                    candidates.Select(a => new MediaClassificationRequest(a.Id, a.FileName, a.ContentType)).ToList());

                if (refuseForContent) return DmRefusalResults.ExplicitContent();
            }

            conversation.UpdatedAt = DateTime.UtcNow;

        }



        var attachments = (await context.Attachments.AsNoTracking().Where(a => dto.Attachments.Contains(a.Id)).ToListAsync()).Select(a => new MinimalAttachmentContract()
        {
            Id = a.Id,
            FileName = a.FileName,
            ContentType = a.ContentType,
            ThumbnailUrl = "https://api.venta.gg/api/v1/messaging/attachments/" + a.Id + "/thumbnail",
            ThumbnailId = a.ThumbnailId
        }).ToList();


        var encryptionState = MessageEncryptionState.Plain;

        if (dto.EncryptionState == Domain.Enums.MessageEncryptionState.Encrypted)
        {
            encryptionState = MessageEncryptionState.Encrypted;
        }

        // The context, not the client, decides whether a message may be plaintext.
        var mlsContextId = dto.ConversationId ?? dto.ChannelId;
        var activeGeneration = mlsContextId is null ? null : await mls.GetActiveGenerationAsync(mlsContextId);
        var mlsGeneration = dto.MlsGeneration;

        if (activeGeneration is not null)
        {
            if (encryptionState != MessageEncryptionState.Encrypted)
            {
                return Results.Conflict(new MlsSendConflictDto
                {
                    ContextId = mlsContextId!,
                    Encrypted = true,
                    ActiveGeneration = activeGeneration.Generation,
                    Reason = "This context is end-to-end encrypted; plaintext messages are not accepted.",
                });
            }

            // A client that predates generations sends none, and the only group it could have
            // encrypted against is the live one - so stamp it rather than refusing.
            if (mlsGeneration is { } claimed && claimed != activeGeneration.Generation)
            {
                return Results.Conflict(new MlsSendConflictDto
                {
                    ContextId = mlsContextId!,
                    Encrypted = true,
                    ActiveGeneration = activeGeneration.Generation,
                    Reason = $"Message was encrypted under generation {claimed}; the context is on generation {activeGeneration.Generation}.",
                });
            }

            mlsGeneration = activeGeneration.Generation;
        }
        else if (encryptionState == MessageEncryptionState.Encrypted)
        {
            return Results.Conflict(new MlsSendConflictDto
            {
                ContextId = mlsContextId ?? string.Empty,
                Encrypted = false,
                ActiveGeneration = null,
                Reason = "Encryption is not enabled for this context; nobody joining later could read this message.",
            });
        }


        var message = await bus.InvokeAsync<Message>(new CreateMessageCommand()
        {
            AuthorId = userId,
            AuthorIdType = authorIdType,
            Content = Encoding.UTF8.GetBytes(dto.Content),
            ChannelId = dto.ChannelId,
            ConversationId = dto.ConversationId,
            Attachments = attachments,
            InReplyTo = dto.InReplyTo,
            Mentions = mentions,
            RoleMentions = roleMentions,
            MentionsEveryone = mentionsEveryone,
            MentionsHere = mentionsHere,
            EncryptionState = encryptionState,
            MlsEpoch = dto.MlsEpoch,
            MlsSequenceNumber = dto.MlsSequenceNumber,
            MlsGeneration = mlsGeneration,
            SenderDeviceId = dto.SenderDeviceId
        });

        // No cascaded event here - CreateMessageCommandHandler already raised the MessageCreated
        // for this message (see the remarks on this method).
        return Results.Created($"/api/v1/messaging/{message.Id}", message.ToFacet<Message, MessageDto>());
    }

    /// <summary>Hard cap on how many users or roles one message may mention, matching the limit
    /// Discord documents on allowed_mentions. Every entry costs per-recipient work downstream, so
    /// this is what stops one request buying an unbounded fan-out.</summary>
    public const int MaxMentionsPerMessage = 100;

    /// <summary>Deduplicates and caps a client-supplied mention list.</summary>
    private static List<string> Truncate(IEnumerable<string>? ids, int max) =>
        ids is null ? [] : ids.Distinct(StringComparer.Ordinal).Take(max).ToList();

    /// <summary>Hard cap on one bulk-delete call, matching Discord's.</summary>
    private const int MaxBulkDeleteMessages = 100;

    /// <summary>
    /// Moderator sweep of up to <see cref="MaxBulkDeleteMessages"/> messages in one channel.
    /// </summary>
    [WolverinePost("/api/v1/messaging/bulk-delete")]
    public async Task<IResult> BulkDeleteMessages(BulkDeleteMessagesDto dto, [NotBody] IMessageRepository repo,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus, [NotBody] ILogger<MessagingEndpoints> logger)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(dto.ChannelId)) return Results.BadRequest("channelId is required");

        var requestedIds = dto.MessageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (requestedIds.Count == 0) return Results.BadRequest("messageIds is required");
        if (requestedIds.Count > MaxBulkDeleteMessages)
            return Results.BadRequest($"messageIds may not exceed {MaxBulkDeleteMessages} entries.");

        var permission = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest
            {
                ChannelId = dto.ChannelId,
                UserId = userId,
                Permission = ExternalPermission.DeleteAnyMessage,
            });

        if (!permission.IsAllowed) return Results.Forbid();

        var resolved = await Task.WhenAll(requestedIds.Select(repo.GetMessageAsync));

        // Silently skipping ids that don't exist or belong elsewhere would make a partially-wrong
        // request look like a clean sweep, so the count of what was actually removed is returned
        // and any discrepancy is logged - the caller can diff it against what it asked for.
        var deletable = resolved
            .Where(m => m is not null && m!.ChannelId == dto.ChannelId)
            .Select(m => m!)
            .ToList();

        if (deletable.Count != requestedIds.Count)
        {
            logger.LogInformation(
                "Bulk delete in channel {ChannelId} by {UserId}: {Requested} requested, {Deletable} resolved to this channel",
                dto.ChannelId, userId, requestedIds.Count, deletable.Count);
        }

        if (deletable.Count == 0) return Results.Ok(new { deleted = 0, messageIds = Array.Empty<string>() });

        await repo.DeleteMessagesAsync(deletable);

        foreach (var message in deletable)
        {
            await bus.PublishAsync(new MessageDeleted
            {
                MessageId = message.Id,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                AuthorId = message.AuthorId,
            });
        }

        var deletedIds = deletable.Select(m => m.Id).ToList();
        await bus.PublishAsync(new Guild.Contracts.Bus.Events.MessagesBulkDeletedForChannel
        {
            ChannelId = dto.ChannelId,
            MessageIds = deletedIds,
            ActorUserId = userId,
        });

        return Results.Ok(new { deleted = deletedIds.Count, messageIds = deletedIds });
    }

    [WolverineDelete("/api/v1/messaging/{messageId}")]
    public async Task<(IResult, MessageDeleted?)> DeleteMessage(string messageId, [NotBody] IMessageRepository repo,
        [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return (Results.Unauthorized(), null);

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return (Results.NotFound(), null);

        // Authors may always delete their own.
        if (message.AuthorId != userId)
        {
            if (string.IsNullOrWhiteSpace(message.ChannelId)) return (Results.Forbid(), null);

            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest
                {
                    ChannelId = message.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.DeleteAnyMessage,
                });

            if (!response.IsAllowed) return (Results.Forbid(), null);
        }

        await repo.DeleteMessageAsync(message);
        return (Results.Accepted(), new MessageDeleted()
        {
            MessageId = messageId,
            ChannelId = message.ChannelId,
            ConversationId = message.ConversationId,
            AuthorId = message.AuthorId,
        });
    }

    [WolverinePut("/api/v1/messaging/{messageId}")]
    public async Task<IResult> UpdateMessageAsync(string messageId, UpdateMessageDto dto, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var result = await bus.InvokeAsync<UpdateMessageResponse>(new UpdateMessageCommand
        {
            MessageId = messageId,
            RequestingAuthorId = userId,
            Content = Encoding.UTF8.GetBytes(dto.Content),
        });

        if (result.NotFound) return Results.NotFound();
        if (result.Forbidden) return Results.Forbid();

        return Results.Accepted(value: new { messageId, content = dto.Content });
    }

    /// <summary>
    /// Hides or restores this message's link previews (docs/specs/message-previews.md).
    /// </summary>
    [WolverinePatch("/api/v1/messaging/{messageId}/embeds")]
    public async Task<IResult> SuppressMessageEmbeds(string messageId, SuppressEmbedsDto dto,
        [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return Results.NotFound();

        // Same ladder as DeleteMessage above, and for the same reason: the author always controls
        // their own message, and a moderator holding DeleteAnyMessage can already remove the whole
        // message - so letting them remove just the preview is strictly less power, not more.
        if (message.AuthorId != userId)
        {
            if (string.IsNullOrWhiteSpace(message.ChannelId)) return Results.Forbid();

            var permission = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest
                {
                    ChannelId = message.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.DeleteAnyMessage,
                });

            if (!permission.IsAllowed) return Results.Forbid();
        }

        var flags = dto.Suppress
            ? MessageFlags.With(message.Flags, MessageFlags.SuppressEmbeds)
            : MessageFlags.Without(message.Flags, MessageFlags.SuppressEmbeds);

        // Drop only what the unfurler produced.
        var remaining = dto.Suppress
            ? GeneratedEmbeds.RemoveGenerated(message.EmbedsJson)
            : null;

        var result = await bus.InvokeAsync<UpdateMessageResponse>(new UpdateMessageCommand
        {
            MessageId = messageId,
            RequestingAuthorId = userId,
            AuthorizationAlreadyChecked = true,
            IsAuthorEdit = false,
            Flags = flags,
            EmbedsJson = remaining,
        });

        if (result.NotFound) return Results.NotFound();
        if (result.Forbidden) return Results.Forbid();

        // Unsuppressing re-queues the unfurl, so the preview the user asked to see comes back
        // without them having to re-post the link.
        if (!dto.Suppress)
        {
            await bus.PublishAsync(new UnfurlMessageLinks
            {
                MessageId = messageId,
                ContextId = message.ContextId,
            });
        }

        return Results.Accepted(value: new { messageId, suppressed = dto.Suppress });
    }

    [WolverinePost("/api/v1/messaging/{messageId}/pin")]
    public async Task<IResult> PinMessage(string messageId, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService conversationPermissionService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(message.ChannelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = message.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.PinMessages
                });

            if (!response.IsAllowed) return Results.Forbid();
        }
        else if (!string.IsNullOrWhiteSpace(message.ConversationId))
        {
            if (!await conversationPermissionService.HasPermission(userId, message.ConversationId)) return Results.Forbid();
        }
        else
        {
            return Results.NotFound();
        }

        var result = await bus.InvokeAsync<PinMessageResponse>(new PinMessageCommand
        {
            MessageId = messageId,
            RequestingUserId = userId,
        });

        if (result.NotFound) return Results.NotFound();
        return Results.Ok(result);
    }

    [WolverineDelete("/api/v1/messaging/{messageId}/pin")]
    public async Task<IResult> UnpinMessage(string messageId, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService conversationPermissionService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(message.ChannelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = message.ChannelId,
                    UserId = userId,
                    Permission = ExternalPermission.PinMessages
                });

            if (!response.IsAllowed) return Results.Forbid();
        }
        else if (!string.IsNullOrWhiteSpace(message.ConversationId))
        {
            if (!await conversationPermissionService.HasPermission(userId, message.ConversationId)) return Results.Forbid();
        }
        else
        {
            return Results.NotFound();
        }

        var result = await bus.InvokeAsync<PinMessageResponse>(new UnpinMessageCommand
        {
            MessageId = messageId,
            RequestingUserId = userId,
        });

        if (result.NotFound) return Results.NotFound();
        return Results.Ok(result);
    }

    [WolverineGet("/api/v1/messaging/pins")]
    public async Task<IResult> GetPinnedMessages([FromQuery] string? channelId, [FromQuery] string? conversationId,
        [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user,
        [NotBody] ConversationPermissionService conversationPermissionService, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(channelId) && string.IsNullOrWhiteSpace(conversationId)) return Results.BadRequest();

        string contextId;
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest()
                {
                    ChannelId = channelId,
                    UserId = userId,
                    Permission = ExternalPermission.ViewChannel
                });

            if (!response.IsAllowed) return Results.Forbid();
            contextId = channelId;
        }
        else
        {
            if (!await conversationPermissionService.HasPermission(userId, conversationId!)) return Results.Forbid();
            contextId = conversationId!;
        }

        var pinned = await repo.GetPinnedMessagesAsync(contextId);
        return Results.Ok(pinned.Select(m => m.ToFacet<Message, MessageDto>()));
    }
}