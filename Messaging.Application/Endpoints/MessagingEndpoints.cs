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
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
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
    [WolverinePost("/api/v1/messaging")]
    public async Task<(IResult, MessageCreated?)> CreateMessage(CreateMessageDto dto,  [NotBody] ScyllaContext ctx, [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext context, [NotBody] IMessageBus bus, [NotBody] IDistributedCache cache, [NotBody] MlsGroupService mls)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(userId is null) return (Results.Unauthorized(), null);

        var authorIdType = user.FindFirstValue("user_type") == "Bot" ? AuthorIdType.Bot : AuthorIdType.User;

        if(string.IsNullOrWhiteSpace(dto.ConversationId) && string.IsNullOrWhiteSpace(dto.ChannelId)) return (Results.BadRequest(), null);

        // Authoritative copies of the client's mention flags. In a guild channel these are
        // downgraded to false unless the author actually holds MentionEveryone; in a DM/group
        // conversation there is no such permission concept, so whatever the client asked for stands.
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

            if(!response.IsAllowed) return (Results.Forbid(), null);

            // @everyone/@here is a permission, not a client decision. Denial *strips the flags*
            // rather than rejecting the send: the flags are what turns the literal text into a
            // ping, and a message that merely contains "@everyone" is a perfectly ordinary
            // message that should still deliver - it just shouldn't notify anyone. Same outcome
            // real Discord produces.
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

                    return (Results.Json(new { error = "automod_blocked", reason = blockedReason }, statusCode: StatusCodes.Status403Forbidden), null);
                }

                // Deliberately the last gate before the message is created: passing the check
                // consumes the author's slowmode window, so anything that can still reject the
                // send (permissions, auto-mod) has to have run first, or a message blocked for an
                // unrelated reason would silently start the cooldown anyway.
                //
                // Bots are exempt for the same reason they bypass auto-mod, and moderators via
                // CanBypassSlowMode.
                if (response.SlowModeSeconds > 0 && !response.CanBypassSlowMode)
                {
                    var retryAfter = await SlowModeGuard.CheckAsync(dto.ChannelId, userId, response.SlowModeSeconds, cache);
                    if (retryAfter is not null)
                    {
                        return (Results.Json(
                            new { error = "slowmode", retry_after = retryAfter.Value, global = false },
                            statusCode: StatusCodes.Status429TooManyRequests), null);
                    }
                }
            }
        }
        else
        {
            var conversation = await context.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == dto.ConversationId);
            if(conversation is null) return (Results.NotFound(), null);
        
            if(conversation.Members.All(m => m.UserId != userId))
            {
                return (Results.Forbid(), null);
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

        // The context, not the client, decides whether a message may be plaintext. A client that has
        // not yet heard encryption was turned on would otherwise post readable content into a room
        // everyone believes is end-to-end encrypted - silently, and irreversibly once stored.
        // Rejecting turns that into a retry.
        var mlsContextId = dto.ConversationId ?? dto.ChannelId;
        var activeGeneration = mlsContextId is null ? null : await mls.GetActiveGenerationAsync(mlsContextId);
        var mlsGeneration = dto.MlsGeneration;

        if (activeGeneration is not null)
        {
            if (encryptionState != MessageEncryptionState.Encrypted)
            {
                return (Results.Conflict(new MlsSendConflictDto
                {
                    ContextId = mlsContextId!,
                    Encrypted = true,
                    ActiveGeneration = activeGeneration.Generation,
                    Reason = "This context is end-to-end encrypted; plaintext messages are not accepted.",
                }), null);
            }

            // A client that predates generations sends none, and the only group it could have
            // encrypted against is the live one - so stamp it rather than refusing. An explicitly
            // wrong generation is a different matter: that ciphertext was sealed to a group which has
            // since been replaced, and nobody in the context can read it.
            if (mlsGeneration is { } claimed && claimed != activeGeneration.Generation)
            {
                return (Results.Conflict(new MlsSendConflictDto
                {
                    ContextId = mlsContextId!,
                    Encrypted = true,
                    ActiveGeneration = activeGeneration.Generation,
                    Reason = $"Message was encrypted under generation {claimed}; the context is on generation {activeGeneration.Generation}.",
                }), null);
            }

            mlsGeneration = activeGeneration.Generation;
        }
        else if (encryptionState == MessageEncryptionState.Encrypted)
        {
            return (Results.Conflict(new MlsSendConflictDto
            {
                ContextId = mlsContextId ?? string.Empty,
                Encrypted = false,
                ActiveGeneration = null,
                Reason = "Encryption is not enabled for this context; nobody joining later could read this message.",
            }), null);
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
            Mentions = dto.Mentions.ToList(),
            RoleMentions = dto.RoleMentions.ToList(),
            MentionsEveryone = mentionsEveryone,
            MentionsHere = mentionsHere,
            EncryptionState = encryptionState,
            MlsEpoch = dto.MlsEpoch,
            MlsSequenceNumber = dto.MlsSequenceNumber,
            MlsGeneration = mlsGeneration,
            SenderDeviceId = dto.SenderDeviceId
        });
        
       
        
        

        return (Results.Created($"/api/v1/messaging/{message.Id}", message.ToFacet<Message, MessageDto>()),
            new MessageCreated()
            {
                MessageId = message.Id,
                ChannelId = dto.ChannelId,
                ConversationId = dto.ConversationId,
                ContextId = message.ContextId,
                Content = message.Content,
                CorrelationId = message.ContextId,
                AuthorId = userId,
                Attachments = message.Attachments,
                InReplyTo = message.InReplyTo,
                EncryptionState = message.EncryptionState,
                Mentions = message.Mentions,
                MlsSequenceNumber = message.MlsSequenceNumber,
                SenderDeviceId = message.SenderDeviceId,
                MlsEpoch = message.MlsEpoch,
            });
    }

    /// <summary>Hard cap on one bulk-delete call, matching Discord's. Also what keeps the
    /// per-message id resolution below affordable - each one is a secondary-index lookup.</summary>
    private const int MaxBulkDeleteMessages = 100;

    /// <summary>Moderator sweep of up to <see cref="MaxBulkDeleteMessages"/> messages in one
    /// channel. Fans out one MessageDeleted per removed message rather than a single bulk event:
    /// that reuses the whole existing per-delete pipeline unchanged (search-index cleanup, the
    /// MESSAGE_DELETE bot dispatch, the forum-post MessageCount decrement) instead of
    /// reimplementing each of those side effects in a bulk-shaped variant that could drift.
    /// Clients additionally get one aggregate realtime push so the UI can remove the whole range
    /// in a single update.</summary>
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

        // Authors may always delete their own. Anyone else needs DeleteAnyMessage, which until now
        // was unreachable through this endpoint - moderators could not remove another member's
        // message at all, in any channel.
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