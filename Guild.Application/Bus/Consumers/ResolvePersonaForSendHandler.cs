using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Entity;
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

        var resolved = await ResolveAsync(request, guildId, personas, permissionService, realtime);
        if (!resolved.IsAllowed) return resolved;

        return await GateClosedSceneAsync(request, guildId, resolved, permissionService, ctx);
    }

    private static async Task<ResolvePersonaForSendResponse> ResolveAsync(
        ResolvePersonaForSendRequest request,
        string guildId,
        PersonaService personas,
        GuildPermissionService permissionService,
        RoleplayRealtimeService realtime)
    {
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

    /// <summary>
    /// In a scene whose policy is Ask, only the cast and whoever holds ManageScenes speak. Keyed on
    /// the scene channel, so the out-of-character companion thread stays open to everyone who can
    /// see the scene.
    /// </summary>
    private static async Task<ResolvePersonaForSendResponse> GateClosedSceneAsync(
        ResolvePersonaForSendRequest request,
        string guildId,
        ResolvePersonaForSendResponse resolved,
        GuildPermissionService permissionService,
        MicroserviceContext ctx)
    {
        var cast = await ctx.Set<SceneState>()
            .AsNoTracking()
            .Where(s => s.ChannelId == request.ChannelId
                        && s.JoinPolicy == SceneJoinPolicy.Ask)
            .Select(s => s.ParticipantPersonaIds)
            .FirstOrDefaultAsync();

        if (cast is null) return resolved;

        // A plain message with no character at all lands here too, which is what makes a closed
        // scene actually closed.
        if (resolved.PersonaId is { } personaId && cast.Contains(personaId, StringComparer.Ordinal))
            return resolved;

        if (await permissionService.CanUserPerformActionOnGuildAsync(
                request.UserId, guildId, ModulePermissions.ManageScenes))
        {
            return resolved;
        }

        return Denied(
            "Only the cast plays in this scene. Ask the GM to bring a character in.", request.Content);
    }

    private static ResolvePersonaForSendResponse Denied(string? error, string? content) => new()
    {
        IsAllowed = false,
        Error = error ?? "You cannot speak as that persona here.",
        Content = content,
    };
}
