using System.Security.Claims;
using Bots.Application.Dtos.Request;
using Bots.Application.Dtos.Response;
using Bots.Domain.Entity;
using Bots.Infrastructure.Persistence;
using Facet.Extensions.EFCore;
using Guild.Contracts;
using Guild.Contracts.Bus.Commands;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints;

[Authorize]
public class BotInstallEndpoint
{
    // Backs the bot portal's guild picker - most people installing a bot have no idea what
    // their guild's raw id is, so let them pick from guilds they actually manage instead of
    // requiring them to type/paste one.
    [WolverineGet("/api/v1/guilds/manageable")]
    public async Task<IResult> GetManageableGuildsAsync([NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var response = await bus.InvokeAsync<ListManageableGuildsForUserResponse>(
            new ListManageableGuildsForUserRequest { UserId = userId });

        return Results.Ok(response.Guilds);
    }

    [WolverineGet("/api/v1/oauth2/authorize")]
    public async Task<IResult> GetAuthorizeInfoAsync(string clientId, ulong permissions, string guildId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var installerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(installerUserId)) return Results.Unauthorized();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.BotUserId == clientId && a.IsEnabled);
        if (app is null) return Results.NotFound();

        var resolved = await bus.InvokeAsync<ResolveInstallablePermissionsResponse>(new ResolveInstallablePermissionsRequest
        {
            InstallerUserId = installerUserId,
            GuildId = guildId,
            RequestedPermissions = permissions,
        });
        if (!resolved.HasManageGuild) return Results.Forbid();

        return Results.Ok(new AuthorizeConsentDto
        {
            ApplicationId = app.BotUserId,
            Name = app.Name,
            IconUrl = app.IconUrl,
            Description = app.Description,
            GuildId = guildId,
            RequestedPermissions = permissions,
            GrantablePermissions = resolved.ClampedPermissions,
        });
    }

    [WolverinePost("/api/v1/oauth2/authorize")]
    public async Task<IResult> ApproveInstallAsync(ApproveInstallDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var installerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(installerUserId)) return Results.Unauthorized();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.BotUserId == dto.ClientId && a.IsEnabled);
        if (app is null) return Results.NotFound();

        var resolved = await bus.InvokeAsync<ResolveInstallablePermissionsResponse>(new ResolveInstallablePermissionsRequest
        {
            InstallerUserId = installerUserId,
            GuildId = dto.GuildId,
            RequestedPermissions = dto.Permissions,
        });
        if (!resolved.HasManageGuild) return Results.Forbid();

        var created = await bus.InvokeAsync<CreateBotGuildMemberResponse>(new CreateBotGuildMemberCommand
        {
            GuildId = dto.GuildId,
            BotUserId = app.BotUserId,
            BotDisplayName = app.Name,
            InstalledByUserId = installerUserId,
            GrantedPermissions = resolved.ClampedPermissions,
        });

        var existingInstall = await ctx.BotInstallations
            .FirstOrDefaultAsync(i => i.BotApplicationId == app.Id && i.GuildId == dto.GuildId);
        if (existingInstall is null)
        {
            ctx.BotInstallations.Add(new BotInstallation
            {
                Id = BotInstallation.GenerateId(),
                BotApplicationId = app.Id,
                GuildId = dto.GuildId,
                InstalledByUserId = installerUserId,
                GrantedPermissions = resolved.ClampedPermissions,
                GuildMemberId = created.GuildMemberId,
                InstalledAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existingInstall.GrantedPermissions = resolved.ClampedPermissions;
        }

        if (!string.IsNullOrWhiteSpace(dto.RedirectUri))
            return Results.Redirect($"{dto.RedirectUri}?guild_id={dto.GuildId}&permissions={resolved.ClampedPermissions}");

        return Results.Ok(new { dto.GuildId, GrantedPermissions = resolved.ClampedPermissions });
    }

    [WolverineGet("/api/v1/applications/{applicationId}/installations")]
    public async Task<IResult> GetInstallationsAsync(string applicationId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var ownerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerUserId)) return Results.Unauthorized();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.Id == applicationId);
        if (app is null) return Results.NotFound();
        if (app.OwnerUserId != ownerUserId) return Results.Forbid();

        var installations = await ctx.BotInstallations
            .Where(i => i.BotApplicationId == applicationId)
            .ToFacetsAsync<BotInstallation, BotInstallationDto>();

        return Results.Ok(installations);
    }

    [WolverineDelete("/api/v1/applications/{applicationId}/installations/{guildId}")]
    public async Task<IResult> UninstallAsync(string applicationId, string guildId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var callerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(callerUserId)) return Results.Unauthorized();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.Id == applicationId);
        if (app is null) return Results.NotFound();

        if (app.OwnerUserId != callerUserId)
        {
            var hasManageGuild = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(new HasUserPermissionToGuildRequest
            {
                UserId = callerUserId,
                GuildId = guildId,
                Permission = ExternalPermission.ManageGuild,
            });
            if (!hasManageGuild.IsAllowed) return Results.Forbid();
        }

        await bus.InvokeAsync(new RemoveBotGuildMemberCommand
        {
            GuildId = guildId,
            BotUserId = app.BotUserId,
            RemovedByUserId = callerUserId,
        });

        var installation = await ctx.BotInstallations
            .FirstOrDefaultAsync(i => i.BotApplicationId == applicationId && i.GuildId == guildId);
        if (installation is not null) ctx.BotInstallations.Remove(installation);

        return Results.NoContent();
    }
}
