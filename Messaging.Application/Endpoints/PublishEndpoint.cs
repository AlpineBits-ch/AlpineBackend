using System.Security.Claims;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Wolverine;
using Wolverine.Http;

namespace Messaging.Application.Endpoints;

/// <summary>Cross-posts a message from an Announcement channel to every channel following it
/// (Discord's "Publish" button) - the fan-out list is Guild-owned data (GuildChannelFollow),
/// fetched over the bus; the actual copies are created locally via the same CreateMessageCommand
/// every other message-create path already uses.</summary>
[Authorize]
public class PublishEndpoint
{
    [WolverinePost("/api/v1/messaging/{messageId}/publish")]
    public async Task<IResult> Publish(string messageId, [NotBody] IMessageRepository repo, [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var message = await repo.GetMessageAsync(messageId);
        if (message is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(message.ChannelId)) return Results.BadRequest("Only channel messages can be published");

        var permissionResponse = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest { ChannelId = message.ChannelId, UserId = userId, Permission = ExternalPermission.PinMessages });
        if (!permissionResponse.IsAllowed) return Results.Forbid();

        var followers = await bus.InvokeAsync<GetChannelFollowersResponse>(new GetChannelFollowersRequest { ChannelId = message.ChannelId });
        if (!followers.IsAnnouncementChannel) return Results.BadRequest("Only messages in Announcement channels can be published");
        if (followers.TargetChannelIds.Count == 0) return Results.Ok(new { published = 0 });

        var authorIdType = message.AuthorIdType switch
        {
            global::Messaging.Domain.Enums.AuthorIdType.Bot => AuthorIdType.Bot,
            global::Messaging.Domain.Enums.AuthorIdType.Webhook => AuthorIdType.Webhook,
            _ => AuthorIdType.User,
        };

        foreach (var targetChannelId in followers.TargetChannelIds)
        {
            await bus.InvokeAsync(new CreateMessageCommand
            {
                Content = message.Content,
                ChannelId = targetChannelId,
                AuthorId = message.AuthorId,
                AuthorIdType = authorIdType,
                EncryptionState = MessageEncryptionState.Plain,
                EmbedsJson = message.EmbedsJson,
                Attachments = message.Attachments.Select(a => new MinimalAttachmentContract
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    ThumbnailUrl = a.ThumbnailUrl,
                    ThumbnailId = a.ThumbnailId,
                }).ToList(),
                // Mentions don't carry meaning across guilds - a @role or @user id from the
                // source guild is meaningless (and potentially a different user entirely) in the
                // target guild, so crossposted copies are stripped to plain content + embeds.
                Mentions = [],
                RoleMentions = [],
                MentionsEveryone = false,
                MentionsHere = false,
            });
        }

        return Results.Ok(new { published = followers.TargetChannelIds.Count });
    }
}
