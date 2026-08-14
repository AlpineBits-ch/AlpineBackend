using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Wolverine;

namespace Bots.Application.Endpoints;

/// <summary>
/// The permission gate every interaction entry point shares: slash commands, component presses,
/// modal submits and autocomplete.
/// </summary>
internal static class InteractionPermissions
{
    /// <summary>Null when the caller may proceed; the Forbid result to return otherwise.</summary>
    public static async Task<IResult?> CheckAsync(IMessageBus bus, string channelId, string userId)
    {
        foreach (var permission in Required)
        {
            var response = await bus.InvokeAsync<HasUserPermissionToChannelResponse>(
                new HasUserPermissionToChannelRequest
                {
                    ChannelId = channelId,
                    UserId = userId,
                    Permission = permission,
                });

            if (!response.IsAllowed) return Results.Forbid();
        }

        return null;
    }

    /// <summary>Ordered so the broader "may you speak here" answer comes first: it is the one a
    /// denied caller is overwhelmingly likely to be failing, and the one whose response the slowmode
    /// fields ride on.</summary>
    private static readonly ExternalPermission[] Required =
    [
        ExternalPermission.SendMessages,
        ExternalPermission.UseApplicationCommands,
    ];
}
