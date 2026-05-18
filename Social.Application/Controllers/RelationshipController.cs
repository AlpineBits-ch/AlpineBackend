using System.Security.Claims;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/relationships")]
public class RelationshipController(MicroserviceContext ctx) : ControllerBase
{

    [HttpGet]
    public async Task<IActionResult> GetRelationships()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var profile = await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        if (userId is null || profile is null) return BadRequest();
        var relationships = await ctx.Relationships
            .Where(r => r.OwnerId == profile.Id)
            .Where(r => r.Status != RelationshipStatus.None)
            .ToFacetsAsync<Relationship, RelationshipDto>();

        return Ok(relationships);
    }
}
