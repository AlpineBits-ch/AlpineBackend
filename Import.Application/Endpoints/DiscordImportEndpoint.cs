using System.Security.Claims;
using AppEnvironment;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Import.Application.Commands;
using Import.Application.Discord;
using Import.Application.Redis;
using Import.Domain.Entity;
using Import.Domain.Enums;
using Import.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Import.Application.Endpoints;

public class StartImportResponseDto
{
    public string AuthorizeUrl { get; set; } = "";
}

public class ImportJobStatusDto
{
    public string ImportJobId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? EchoGuildId { get; set; }
    public string? ErrorMessage { get; set; }
}

public class GuildLinkDto
{
    public string Id { get; set; } = "";
    public string DiscordGuildId { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset? LastSyncedAt { get; set; }
}

/// <summary>
/// Route paths here deliberately omit the "imports" segment - the gateway's imports-route
/// (Echo/Proxy/ProxyConfig.cs) already strips it before forwarding, same convention as
/// bots-route/guild-route. Public URLs are still under /api/v1/imports/** (see the route
/// comments below); only the internal route these attributes register is shorter.
/// </summary>
[Authorize]
public partial class DiscordImportEndpoint
{
    /// <summary>Kicks off the OAuth "add bot to server" flow - the browser is sent to Discord's
    /// own consent screen, requesting only View Channels (bit 0x400). No privileged intents/
    /// permissions are requested since structure import never touches members or messages.</summary>
    [WolverineGet("/api/v1/discord/start")]
    public async Task<StartImportResponseDto> Start(
        [NotBody] ClaimsPrincipal user, [NotBody] DiscordImportStateStore stateStore)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var stateId = Guid.NewGuid().ToString("N");
        await stateStore.SaveAsync(stateId, userId);

        var redirectUri = Uri.EscapeDataString(Env.DiscordImport.PublicBaseUrl + Env.DiscordImport.PublicCallbackPath);
        var authorizeUrl =
            $"https://discord.com/oauth2/authorize?client_id={Env.DiscordImport.ClientId}" +
            "&scope=bot" +
            "&permissions=1024" +
            // Without response_type=code, Discord's bot-add flow shows its own "you're done"
            // confirmation screen and never calls redirect_uri at all - we don't need the
            // authorization code itself (we already have a static bot token), but including
            // response_type=code is what makes Discord actually perform the HTTP redirect back
            // to us (with guild_id appended) instead of just ending the flow in its own UI.
            "&response_type=code" +
            $"&redirect_uri={redirectUri}" +
            $"&state={stateId}";

        return new StartImportResponseDto { AuthorizeUrl = authorizeUrl };
    }

    /// <summary>Discord's redirect target once the guild owner approves the bot-add. Creates the
    /// ImportJob and enqueues the durable command that does the actual fetch-and-build work -
    /// this endpoint itself stays fast, matching how nothing else in this codebase does slow
    /// work synchronously inside an HTTP request.</summary>
    [WolverineGet("/api/v1/discord/callback")]
    [AllowAnonymous]
    public async Task<IResult> Callback(
        string state, string? guild_id,
        [NotBody] DiscordImportStateStore stateStore, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var requestingUserId = await stateStore.ConsumeAsync(state);
        if (requestingUserId is null || string.IsNullOrWhiteSpace(guild_id))
        {
            return Results.BadRequest("Invalid or expired import request.");
        }

        var job = new ImportJob
        {
            Id = ImportJob.GenerateId(),
            DiscordGuildId = guild_id,
            RequestedByUserId = requestingUserId,
            Status = ImportJobStatus.Pending,
        };
        ctx.ImportJobs.Add(job);
        await ctx.SaveChangesAsync();

        await bus.SendAsync(new StartDiscordStructureImportCommand
        {
            ImportJobId = job.Id,
            DiscordGuildId = guild_id,
            RequestedByUserId = requestingUserId,
        });

        return Results.Redirect($"{Env.DiscordImport.ClientReturnUrl}?jobId={job.Id}");
    }

    [WolverineGet("/api/v1/jobs/{jobId}")]
    public async Task<IResult> GetStatus(string jobId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var job = await ctx.ImportJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null || job.RequestedByUserId != userId) return Results.NotFound();

        return Results.Ok(new ImportJobStatusDto
        {
            ImportJobId = job.Id,
            Status = job.Status.ToString(),
            EchoGuildId = job.EchoGuildId,
            ErrorMessage = job.ErrorMessage,
        });
    }

    /// <summary>
    /// Requires ManageGuild on the Echo guild whose link is being read. The gateway applies no
    /// per-guild authorization (no YARP route in this repo sets an AuthorizationPolicy), so the
    /// check has to happen here - otherwise any authenticated user could enumerate any guild's
    /// Discord link and obtain the link id the mutating routes below key on.
    /// </summary>
    [WolverineGet("/api/v1/links")]
    public async Task<IResult> GetLinks(
        string guildId,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        if (!await HasManageGuildAsync(bus, user, guildId)) return Results.Forbid();

        var link = await ctx.GuildLinks.FirstOrDefaultAsync(l => l.EchoGuildId == guildId);
        if (link is null) return Results.Ok(Array.Empty<GuildLinkDto>());

        return Results.Ok(new[]
        {
            new GuildLinkDto
            {
                Id = link.Id,
                DiscordGuildId = link.DiscordGuildId,
                Status = link.Status.ToString(),
                LastSyncedAt = link.LastSyncedAt,
            },
        });
    }

    /// <summary>Body is just the target status - "Paused" or "Active". Requires ManageGuild on the
    /// linked Echo guild, checked here rather than assumed of the gateway: the gateway forwards
    /// this route with no authorization policy of its own.</summary>
    [WolverinePatch("/api/v1/links/{id}")]
    public async Task<IResult> SetLinkStatus(
        string id,
        SetLinkStatusDto dto,
        [NotBody] MicroserviceContext ctx,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        var link = await ctx.GuildLinks.FirstOrDefaultAsync(l => l.Id == id);
        if (link is null) return Results.NotFound();
        if (!await HasManageGuildAsync(bus, user, link.EchoGuildId)) return Results.Forbid();

        if (!Enum.TryParse<GuildLinkStatus>(dto.Status, out var status) || status == GuildLinkStatus.Revoked)
        {
            return Results.BadRequest("Status must be Active or Paused - use DELETE to revoke a link.");
        }

        link.Status = status;
        await ctx.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>Revokes the link and removes the import bot from the Discord server - an
    /// externally-visible, hard-to-undo action, so it requires ManageGuild on the Echo guild.</summary>
    [WolverineDelete("/api/v1/links/{id}")]
    public async Task<IResult> Unlink(
        string id,
        [NotBody] MicroserviceContext ctx,
        [NotBody] DiscordApiClient discordApi,
        [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user)
    {
        var link = await ctx.GuildLinks.FirstOrDefaultAsync(l => l.Id == id);
        if (link is null) return Results.NotFound();
        if (!await HasManageGuildAsync(bus, user, link.EchoGuildId)) return Results.Forbid();

        link.Status = GuildLinkStatus.Revoked;
        await ctx.SaveChangesAsync();

        try
        {
            await discordApi.LeaveGuildAsync(link.DiscordGuildId);
        }
        catch
        {
            // best-effort - the guild admin may have already removed the bot themselves
        }

        return Results.NoContent();
    }
}

public class SetLinkStatusDto
{
    public string Status { get; set; } = "";
}

public partial class DiscordImportEndpoint
{
    /// <summary>
    /// Asks Guild whether the caller holds ManageGuild on <paramref name="echoGuildId"/>. Fails
    /// closed on a missing identity or an unresolvable guild id.
    /// </summary>
    private static async Task<bool> HasManageGuildAsync(IMessageBus bus, ClaimsPrincipal user, string? echoGuildId)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(echoGuildId)) return false;

        var response = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(
            new HasUserPermissionToGuildRequest
            {
                UserId = userId,
                GuildId = echoGuildId,
                Permission = ExternalPermission.ManageGuild,
            });

        return response.IsAllowed;
    }
}
