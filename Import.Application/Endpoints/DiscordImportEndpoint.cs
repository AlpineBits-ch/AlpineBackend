using System.Security.Claims;
using AppEnvironment;
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
public class DiscordImportEndpoint
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

    [WolverineGet("/api/v1/links")]
    public async Task<IResult> GetLinks(string guildId, [NotBody] MicroserviceContext ctx)
    {
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

    /// <summary>Body is just the target status - "Paused" or "Active". Authorization (the caller
    /// must hold ManageGuild on the linked Echo guild) is expected to be enforced by the gateway/
    /// frontend today, the same trust boundary Bots' install-flow endpoints rely on; a follow-up
    /// could call Guild's HasUserPermissionToGuildRequest here directly if that's not enough.</summary>
    [WolverinePatch("/api/v1/links/{id}")]
    public async Task<IResult> SetLinkStatus(string id, SetLinkStatusDto dto, [NotBody] MicroserviceContext ctx)
    {
        var link = await ctx.GuildLinks.FirstOrDefaultAsync(l => l.Id == id);
        if (link is null) return Results.NotFound();
        if (!Enum.TryParse<GuildLinkStatus>(dto.Status, out var status) || status == GuildLinkStatus.Revoked)
        {
            return Results.BadRequest("Status must be Active or Paused - use DELETE to revoke a link.");
        }

        link.Status = status;
        await ctx.SaveChangesAsync();
        return Results.NoContent();
    }

    [WolverineDelete("/api/v1/links/{id}")]
    public async Task<IResult> Unlink(string id, [NotBody] MicroserviceContext ctx, [NotBody] DiscordApiClient discordApi)
    {
        var link = await ctx.GuildLinks.FirstOrDefaultAsync(l => l.Id == id);
        if (link is null) return Results.NotFound();

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
