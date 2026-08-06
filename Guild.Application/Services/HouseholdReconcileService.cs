using Guild.Application.Endpoints;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Periodic sweep that keeps the household modules honest without depending on any single
/// scheduled message having survived.
///
/// Five jobs:
///  1. Generate chore occurrences that are due. Chores are also driven by Wolverine's durable
///     scheduler, but a rota that silently stops because one message was lost is worse than a rota
///     that's occasionally a few minutes late - so this is the backstop, and generation is
///     idempotent via the unique (ChoreId, DueAt) index.
///  2. Remind assignees that a chore is due, deferred past the guild's quiet hours. See
///     ChoreReminderService.
///  3. Warn pantries about stock that's about to go off, at each pantry's own horizon. See
///     PantryExpiryService.
///  4. Expire decisions whose ClosesAt has passed, resolving them from the votes already cast.
///  5. Delete long-lapsed guest role memberships. Expiry itself is enforced on read in
///     GuildPermissionService, so this is only tidying, never the thing access depends on.
///
/// Modelled on VoiceHeartbeatCleanupService.</summary>
public class HouseholdReconcileService(
    IServiceProvider services,
    ILogger<HouseholdReconcileService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>How long a lapsed guest membership is kept before deletion.</summary>
    private static readonly TimeSpan GuestRetention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
                var rotation = scope.ServiceProvider.GetRequiredService<ChoreRotationService>();

                await GenerateDueChoresAsync(ctx, rotation, stoppingToken);

                // After generation, so an occurrence created this pass can be reminded on the same
                // pass rather than five minutes later.
                await scope.ServiceProvider.GetRequiredService<ChoreReminderService>()
                    .SendDueRemindersAsync(stoppingToken);

                await scope.ServiceProvider.GetRequiredService<PantryExpiryService>()
                    .SendDueWarningsAsync(stoppingToken);

                await ExpireDecisionsAsync(ctx, stoppingToken);
                await PurgeLapsedGuestRolesAsync(ctx, stoppingToken);
            }
            catch (Exception e)
            {
                // Never let one bad sweep kill the loop - the next tick retries everything.
                logger.LogError(e, "Household reconcile sweep failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    private static async Task GenerateDueChoresAsync(
        MicroserviceContext ctx, ChoreRotationService rotation, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await ctx.Chores
            .Where(c => !c.IsPaused && c.NextDueAt <= now)
            // Oldest first, so a guild past the batch cap makes progress on its most overdue rota
            // rather than on whatever the planner happened to return.
            .OrderBy(c => c.NextDueAt)
            .Take(200)
            .ToListAsync(ct);

        foreach (var chore in due) await rotation.StageNextOccurrenceAsync(chore);

        if (due.Count > 0) await ctx.SaveChangesAsync(ct);
    }

    private static async Task ExpireDecisionsAsync(MicroserviceContext ctx, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var expired = await ctx.Decisions
            .Include(d => d.Options)
            .Include(d => d.Votes)
            .Where(d => d.Status == DecisionStatus.Open && d.ClosesAt != null && d.ClosesAt <= now)
            .Take(200)
            .ToListAsync(ct);

        foreach (var decision in expired)
        {
            var (status, winningOptionId) = decision.Resolve();
            decision.Status = status;
            decision.OutcomeOptionId = winningOptionId;
        }

        if (expired.Count > 0) await ctx.SaveChangesAsync(ct);
    }

    private static async Task PurgeLapsedGuestRolesAsync(MicroserviceContext ctx, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - GuestRetention;

        var lapsed = await ctx.RoleMembers
            .Where(rm => rm.ExpiresAt != null && rm.ExpiresAt < cutoff)
            .Take(500)
            .ToListAsync(ct);

        if (lapsed.Count == 0) return;

        ctx.RoleMembers.RemoveRange(lapsed);
        await ctx.SaveChangesAsync(ct);
    }
}
