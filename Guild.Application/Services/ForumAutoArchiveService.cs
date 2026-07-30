using Echo.Realtime;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Services;

/// <summary>Archives forum posts whose auto-archive deadline has passed.
///
/// A sweep rather than a scheduled job per post: posts slide their deadline on every message, so
/// per-post timers would be cancelled and rescheduled constantly for no benefit. The cost is
/// latency - a post can sit a few minutes past its deadline before flipping - which is why the
/// frontend guide tells clients not to render a live countdown against AutoArchiveAt.</summary>
public class ForumAutoArchiveService(
    IHubContext<EchoRealtimeHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<ForumAutoArchiveService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Bounded so one sweep can't turn into an unbounded write burst after downtime;
    /// the backlog just drains over the following passes.</summary>
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
                logger.LogError(ex, "Forum auto-archive sweep failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var hydrate = scope.ServiceProvider.GetRequiredService<GuildHydrateService>();

        var now = DateTimeOffset.UtcNow;

        var due = await ctx.Channels
            .Where(c => c.Type == ChannelType.Thread
                        && !c.IsArchived
                        && c.AutoArchiveAt != null
                        && c.AutoArchiveAt < now)
            .OrderBy(c => c.AutoArchiveAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var post in due) post.IsArchived = true;

        // BackgroundService runs outside the Wolverine request pipeline, so nothing commits this
        // for us - unlike the endpoints and handlers, which the EF middleware wraps.
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Auto-archived {Count} forum post(s)", due.Count);

        foreach (var guildGroup in due.GroupBy(p => p.GuildId))
        {
            var presence = await hydrate.GetGuildPresenceAsync(guildGroup.Key);
            var userIds = presence.Select(p => p.UserId).ToList();

            foreach (var post in guildGroup)
            {
                await hub.Clients.Users(userIds).SendAsync("guild.ThreadUpdated", new
                {
                    ChannelId = post.Id,
                    ParentChannelId = post.ParentChannelId,
                    GuildId = post.GuildId,
                    Name = post.Name,
                    IsPinned = post.IsPinned,
                    IsLocked = post.IsLocked,
                    Archived = true,
                }, ct);

                await bus.PublishAsync(new ThreadUpdatedForBots
                {
                    ChannelId = post.Id,
                    GuildId = post.GuildId,
                    ParentChannelId = post.ParentChannelId ?? "",
                    Name = post.Name,
                    Archived = true,
                });
            }
        }
    }
}
