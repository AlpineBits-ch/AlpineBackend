using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>
/// Removes temporary memberships whose grace window has run out - the only path that removes one.
/// </summary>
public class TemporaryMembershipSweepService(
    IHubContext<EchoRealtimeHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<TemporaryMembershipSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Bounded so a backlog after downtime drains over several passes instead of becoming
    /// one write burst - and so one guild's mass departure cannot starve the rest.</summary>
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Temporary membership sweep failed");
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var hydrate = scope.ServiceProvider.GetRequiredService<GuildHydrateService>();
        var permissions = scope.ServiceProvider.GetRequiredService<GuildPermissionService>();

        await SweepWithAsync(ctx, bus, hydrate, permissions, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>The sweep itself, with its dependencies passed in so it can be driven directly by a
    /// test without a host or a scope factory.</summary>
    internal async Task SweepWithAsync(MicroserviceContext ctx, IMessageBus bus, GuildHydrateService hydrate,
        GuildPermissionService permissions, DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await ctx.GuildMembers
            .Where(m => m.TemporaryEvictionDueAt != null && m.TemporaryEvictionDueAt <= now)
            .OrderBy(m => m.TemporaryEvictionDueAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        // Re-checked here and not only at scheduling time.
        var memberIds = due.Select(m => m.Id).ToList();
        var earnedARole = (await ctx.RoleMembers
                .Where(rm => memberIds.Contains(rm.MemberId) && rm.Role.Type != RoleType.Everyone)
                .Select(rm => rm.MemberId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var evicted = new List<Domain.Entity.GuildMember>();

        foreach (var member in due)
        {
            if (earnedARole.Contains(member.Id))
            {
                member.TemporaryEvictionDueAt = null;
                continue;
            }

            ctx.GuildMembers.Remove(member);
            evicted.Add(member);
        }

        // BackgroundService runs outside the Wolverine request pipeline, so nothing commits this for
        // us - unlike the endpoints and handlers, which the EF middleware wraps.
        await ctx.SaveChangesAsync(ct);

        if (evicted.Count == 0) return;

        logger.LogInformation("Swept {Count} temporary membership(s)", evicted.Count);

        foreach (var member in evicted)
        {
            await permissions.InvalidateUserPermissionsCacheAsync(member.GuildId, member.UserId);

            var presence = await hydrate.GetGuildPresenceAsync(member.GuildId);
            var audience = presence.Select(p => p.UserId).Append(member.UserId).Distinct(StringComparer.Ordinal).ToList();

            await hub.Clients.Users(audience).SendAsync("guild.MemberLeft",
                new { GuildId = member.GuildId, UserId = member.UserId }, ct);

            await bus.PublishAsync(new MemberRemovedForBots
            {
                GuildId = member.GuildId,
                UserId = member.UserId,
                Reason = "TemporaryMembershipEnded",
            });
        }
    }
}
