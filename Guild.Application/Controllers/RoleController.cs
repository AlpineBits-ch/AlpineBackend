using System.Security.Claims;
using Facet.Extensions.EFCore;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Controllers;

[ApiController]
[Route("api/v1/roles")]
public class RoleController(MicroserviceContext ctx, GuildPermissionService permissionService, ILogger<GuildController> logger): ControllerBase
{
    /// <summary>The largest page this endpoint will hand out.</summary>
    private const int MaxPageSize = 100;

    [HttpGet("{roleId}/members")]
    public async Task<IActionResult> GetRoleMembersAsync(string roleId, [FromQuery] int take = 10, [FromQuery] int skip = 0)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var role = await ctx.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId);
        if (role == null)
        {
            return NotFound();
        }

        if(!await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ViewChannel))
        {
            return Forbid();
        }

        var roleMembers = await ctx.RoleMembers
            .Where(x => x.RoleId == roleId)
            .OrderBy(x => x.Id)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .ToFacetsAsync<RoleMember, RoleMemberDto>();

        return Ok(roleMembers);
    }
}
