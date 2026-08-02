using System.Security.Claims;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Identity.Application.Endpoints;

public class IdentityAuditEventDto
{
    public string Id { get; set; } = null!;

    /// <summary>One of <see cref="IdentityAuditActions"/>.</summary>
    public string Action { get; set; } = null!;

    /// <summary>The device the action was performed from, as derived from the caller's session -
    /// never a value the actor supplied.</summary>
    public string? ClientDeviceId { get; set; }

    public string? DeviceName { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class IdentityAuditPageDto
{
    public List<IdentityAuditEventDto> Events { get; set; } = [];

    /// <summary>Pass back as <c>before</c> to fetch the next page. Null when the log is exhausted.</summary>
    public DateTimeOffset? NextBefore { get; set; }
}

/// <summary>The account's security log, readable by the account.</summary>
[Authorize]
public class IdentityAuditEndpoint
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    [WolverineGet("api/v1/identity/audit-events")]
    public static async Task<IResult> List(
        [FromQuery] int? limit,
        [FromQuery] DateTimeOffset? before,
        [FromQuery] string? action,
        [NotBody] ClaimsPrincipal principal,
        [NotBody] MicroserviceContext ctx)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var query = ctx.IdentityAuditEvents.AsNoTracking().Where(e => e.UserId == userId);

        if (before is { } cutoff) query = query.Where(e => e.CreatedAt < cutoff);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(e => e.Action == action);

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(take + 1)
            .ToListAsync();

        var page = rows.Take(take).ToList();

        // Device names, so the UI can say "your laptop" rather than an opaque client id.
        var deviceIds = page.Select(e => e.ClientDeviceId).Where(d => d is not null).Distinct().ToList();
        var names = deviceIds.Count == 0
            ? new Dictionary<string, string>()
            : await ctx.UserDevices.AsNoTracking()
                .Where(d => d.UserId == userId && deviceIds.Contains(d.ClientDeviceId))
                .ToDictionaryAsync(d => d.ClientDeviceId, d => d.DeviceName);

        return Results.Ok(new IdentityAuditPageDto
        {
            Events = page.Select(e => new IdentityAuditEventDto
            {
                Id = e.Id,
                Action = e.Action,
                ClientDeviceId = e.ClientDeviceId,
                DeviceName = e.ClientDeviceId is { } d && names.TryGetValue(d, out var name) ? name : null,
                Detail = e.Detail,
                IpAddress = e.IpAddress,
                CreatedAt = e.CreatedAt,
            }).ToList(),
            NextBefore = rows.Count > take ? page[^1].CreatedAt : null,
        });
    }
}
