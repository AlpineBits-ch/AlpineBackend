using System.Security.Claims;
using Bots.Application.Dtos.Request;
using Bots.Application.Dtos.Response;
using Bots.Domain.Entity;
using Bots.Infrastructure.Persistence;
using Bots.Application.Middleware;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Identity.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Authorization;
using Ids;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Bots.Application.Endpoints;

[Authorize]
public class BotApplicationEndpoint
{
    // NOTE: routes here omit the "bots" segment on purpose - the gateway's bots-route strips
    // it before forwarding (see Echo/Proxy/ProxyConfig.cs), so a client-facing URL of
    // /api/v1/bots/applications arrives here as /api/v1/applications.
    [WolverinePost("/api/v1/applications")]
    public async Task<IResult> CreateBotApplicationAsync(CreateBotApplicationDto dto,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var ownerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerUserId)) return Results.Unauthorized();

        // Minted here (not by Identity) so the whole create-application flow is idempotent under
        // retry - see CreateBotAccountCommandHandler.
        var botUserId = Identifier.New("user");

        var account = await bus.InvokeAsync<CreateBotAccountResponse>(new CreateBotAccountCommand
        {
            BotUserId = botUserId,
            Name = dto.Name,
        });

        var app = new BotApplication
        {
            Id = BotApplication.GenerateId(),
            OwnerUserId = ownerUserId,
            BotUserId = botUserId,
            Name = dto.Name,
            Description = dto.Description,
            IconUrl = dto.IconUrl,
            DefaultPermissions = dto.DefaultPermissions,
        };
        ctx.BotApplications.Add(app);

        return Results.Created($"/api/v1/bots/applications/{app.Id}", new BotApplicationCreatedDto
        {
            ApplicationId = app.Id,
            ClientId = botUserId,
            ClientSecret = account.ClientSecret,
            CompatToken = DiscordCompatToken.Pack(botUserId, account.ClientSecret),
            Name = app.Name,
        });
    }

    [WolverineGet("/api/v1/applications")]
    public async Task<IResult> GetOwnedBotApplicationsAsync(
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var ownerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerUserId)) return Results.Unauthorized();

        var apps = await ctx.BotApplications
            .Where(a => a.OwnerUserId == ownerUserId)
            .ToFacetsAsync<BotApplication, BotApplicationDto>();

        return Results.Ok(apps);
    }

    [WolverineGet("/api/v1/applications/{applicationId}")]
    public async Task<IResult> GetBotApplicationAsync(string applicationId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx)
    {
        var ownerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerUserId)) return Results.Unauthorized();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.Id == applicationId);
        if (app is null) return Results.NotFound();
        if (app.OwnerUserId != ownerUserId) return Results.Forbid();

        return Results.Ok(app.ToFacet<BotApplication, BotApplicationDto>());
    }

    [WolverineDelete("/api/v1/applications/{applicationId}")]
    public async Task<IResult> DisableBotApplicationAsync(string applicationId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var ownerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerUserId)) return Results.Unauthorized();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.Id == applicationId);
        if (app is null) return Results.NotFound();
        if (app.OwnerUserId != ownerUserId) return Results.Forbid();

        await bus.InvokeAsync(new DisableBotAccountCommand { BotUserId = app.BotUserId });
        app.IsEnabled = false;

        return Results.NoContent();
    }

    [WolverinePost("/api/v1/applications/{applicationId}/reset-secret")]
    public async Task<IResult> ResetBotSecretAsync(string applicationId,
        [NotBody] ClaimsPrincipal user, [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var ownerUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerUserId)) return Results.Unauthorized();

        var app = await ctx.BotApplications.FirstOrDefaultAsync(a => a.Id == applicationId);
        if (app is null) return Results.NotFound();
        if (app.OwnerUserId != ownerUserId) return Results.Forbid();

        var reset = await bus.InvokeAsync<ResetBotSecretResponse>(new ResetBotSecretCommand { BotUserId = app.BotUserId });
        if (!reset.Found) return Results.NotFound();

        return Results.Ok(new ResetSecretDto
        {
            ClientId = app.BotUserId,
            ClientSecret = reset.ClientSecret,
            CompatToken = DiscordCompatToken.Pack(app.BotUserId, reset.ClientSecret),
        });
    }
}
