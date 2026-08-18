using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Guild's half of the persona send path: Messaging owns the message, Guild owns who may wear
/// which costume.
/// </summary>
public class ResolvePersonaForSendHandler
{
    public static async Task<ResolvePersonaForSendResponse> Handle(
        ResolvePersonaForSendRequest request,
        PersonaService personas,
        GuildPermissionService permissionService,
        RoleplayRealtimeService realtime,
        MicroserviceContext ctx)
    {
        var guildId = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == request.ChannelId)
            .Select(c => c.GuildId)
            .FirstOrDefaultAsync();

        // A conversation, or a channel Guild does not know: nothing to resolve, and an explicit
        // persona id there is a client error rather than a silent plain message.
        if (string.IsNullOrWhiteSpace(guildId))
        {
            return string.IsNullOrWhiteSpace(request.PersonaId)
                ? new ResolvePersonaForSendResponse { Content = request.Content }
                : Denied("Personas can only be used in a guild channel.", request.Content);
        }

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Personas))
        {
            return string.IsNullOrWhiteSpace(request.PersonaId)
                ? new ResolvePersonaForSendResponse { Content = request.Content }
                : Denied("This guild does not have personas switched on.", request.Content);
        }

        if (!await permissionService.CanUserPerformActionOnGuildAsync(
                request.UserId, guildId, ModulePermissions.UsePersonas))
        {
            return string.IsNullOrWhiteSpace(request.PersonaId)
                ? new ResolvePersonaForSendResponse { Content = request.Content }
                : Denied("You do not have permission to use personas here.", request.Content);
        }

        var resolution = await personas.ResolveForSendAsync(new PersonaSendContext
        {
            UserId = request.UserId,
            GuildId = guildId,
            ChannelId = request.ChannelId,
            PersonaId = request.PersonaId,
            Content = request.Content,
        });

        // Sticky moves the latched character as play goes on, so the only way a second device finds
        // out it is now speaking as somebody else is this.
        if (resolution.StickyPersonaId is { } latched)
        {
            await realtime.AutoproxyChangedAsync(
                request.UserId, guildId, request.ChannelId, AutoproxyMode.Sticky, latched);
        }

        return resolution.Outcome switch
        {
            PersonaResolutionOutcome.Forbidden => Denied(resolution.Error, resolution.Content),
            PersonaResolutionOutcome.Resolved => new ResolvePersonaForSendResponse
            {
                Content = resolution.Content,
                PersonaId = resolution.PersonaId,
                AuthorDisplayName = resolution.DisplayName,
                AuthorAvatarUrl = resolution.AvatarUrl,
            },
            _ => new ResolvePersonaForSendResponse { Content = resolution.Content },
        };
    }

    private static ResolvePersonaForSendResponse Denied(string? error, string? content) => new()
    {
        IsAllowed = false,
        Error = error ?? "You cannot speak as that persona here.",
        Content = content,
    };
}
