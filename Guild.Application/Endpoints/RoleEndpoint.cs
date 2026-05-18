using System.Security.Claims;
using Facet.Extensions;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Wolverine.Http;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Application.Endpoints;

[Authorize]
public class RoleEndpoint()
{
    [WolverinePost("/api/v1/guilds/{guildId}/roles")]
    public async Task<IResult> CreateRoleAsync(string guildId, CreateRoleParams parameters, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManagePermissions);
        if (!isAuthorized) return Results.Forbid();

        var role = Role.Create(new CreateRoleParams()
        {
            Name = parameters.Name,
            Description = parameters.Description,
            GuildId = guildId,
            Color = parameters.Color,
            Type = parameters.Type,
            Permissions = parameters.Permissions,
        });
        
        
        ctx.Roles.Add(role);
        
        return Results.Ok(role.ToFacet<Role, RoleDto>());
    }
    
    [WolverinePatch("/api/v1/roles/{roleId}")]
    public async Task<(IResult, RoleUpdated?)> UpdateRoleAsync(string roleId, UpdateRoleDto roleDto, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);
        
        var role = ctx.Roles.FirstOrDefault(x => x.Id == roleId);
        
        if(role == null) return (Results.NotFound(), null);
        
        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);
        
        role.Permissions = roleDto.Permissions;
        role.Name = roleDto.Name;
        role.Description = roleDto.Description;
        role.Color = roleDto.Color;
        
        
        return (Results.Ok(), new RoleUpdated()
        {
          RoleId  = roleId,
          GuildId = role.GuildId,
        });
    }

    [WolverinePut("/api/v1/roles/{roleId}/members/{memberId}")]
    public async Task<(IResult, RoleUpdated?)> AddMemberToRoleAsync(string roleId, string memberId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);
        
        var role = ctx.Roles.Include(r => r.Members).FirstOrDefault(x => x.Id == roleId);
        
        if (role == null) return (Results.NotFound(), null);
        
        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);


        var created = DateTime.UtcNow;
        var roleMember = new RoleMember()
        {
            Id = RoleMember.GenerateId(),
            UpdatedAt = created,
            CreatedAt = created,
            RoleId = roleId,
            MemberId = memberId
        };
        
        ctx.RoleMembers.Add(roleMember);

        return (Results.Accepted(), new RoleUpdated()
        {
          RoleId  = roleId,
          GuildId = role.GuildId,
          MemberId = memberId,
        });
    }
    
    [WolverineDelete("/api/v1/roles/{roleId}/members/{memberId}")]
    public async Task<(IResult, RoleUpdated?)> RemoveMemberFromRoleAsync(string roleId, string memberId, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user, [NotBody] GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);
        
        var role = await ctx.Roles.Include(role => role.Members).FirstOrDefaultAsync(x => x.Id == roleId);
        
        if (role == null) return (Results.NotFound(), null);
        
        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);
        
        var member = role.Members.FirstOrDefault(x => x.MemberId == memberId);
        if(member == null) return (Results.NotFound(), null);

        role.Members.Remove(member);
        return (Results.Accepted(), new RoleUpdated()
        {
          RoleId  = roleId,
          GuildId = role.GuildId,
          MemberId = memberId,
        });
    }
    
    
    [WolverineDelete("/api/v1/roles/{roleId}")]
    public async Task<(IResult, RoleUpdated?)> DeleteRoleAsync(string roleId, [NotBody] MicroserviceContext ctx,  [NotBody] ClaimsPrincipal user, [NotBody]GuildPermissionService permissionService)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(string.IsNullOrWhiteSpace(userId)) return (Results.Unauthorized(), null);
        
        var role = ctx.Roles.FirstOrDefault(x => x.Id == roleId);
        
        if(role == null) return (Results.NotFound(), null);

        var isAuthorized = await permissionService.CanUserPerformActionOnGuildAsync(userId, role.GuildId, Permissions.ManagePermissions);
        if (!isAuthorized) return (Results.Forbid(), null);
        
        ctx.Roles.Remove(role);
        return (Results.Ok(), new RoleUpdated()
        {
            RoleId  = roleId,
            GuildId = role.GuildId,
            MemberId = null,
        });
    }
    
    
}