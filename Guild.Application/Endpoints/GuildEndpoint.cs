using System.Security.Claims;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence.Durability;

namespace Guild.Application.Endpoints;

[Authorize]
public class GuildEndpoint
{
    [WolverinePost("/api/v1/guilds")]
    public async Task<IResult> CreateGuild(CreateGuildDto dto, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody] IMessageBus bus)
    {

        var profileResponse = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = user.FindFirstValue(ClaimTypes.NameIdentifier)!
        });

        if(profileResponse.Profile is null) return Results.BadRequest("User not found");
        
        var searchValue = profileResponse.Profile.UserName! + "#" + profileResponse.Profile.Hash;
        
        var guild = Domain.Aggregates.Guild.Create(new CreateGuildParams()
        {
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            OwnerSearchValue = searchValue.ToUpperInvariant()
        });
        
        ctx.Guilds.Add(guild);
        
        
        // because of the sys channel we have to do some hacks 

        var sysChannelId = guild.SystemChannelId;
        guild.SystemChannelId = null;
        await ctx.SaveChangesAsync();
        guild.SystemChannelId = sysChannelId;
        
        
        return Results.Ok(guild.ToFacet<Domain.Aggregates.Guild, GuildDto>());
    }
    
    
    
    [WolverineDelete("/api/v1/guilds/{id}")]
    public async Task<IResult> DeleteGuild(string id)
    {
        return Results.Ok();
    }
    
    [WolverinePatch("/api/v1/guilds/{id}")]
    public async Task<IResult> UpdateGuild(string id, [NotBody] MicroserviceContext context)
    {
        var guild = await context.Guilds.Include(g => g.Channels)
            .Include(g => g.Categories)
            .ThenInclude(c => c.Channels)
            .Where(g => g.Id == id).FirstOrDefaultAsync();
        return Results.Ok(guild?.ToFacet<Domain.Aggregates.Guild, GuildDto>());
    }
   
}