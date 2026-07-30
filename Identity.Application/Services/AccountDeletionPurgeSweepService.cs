using AppEnvironment;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Identity.Application.Services;

/// <summary>
/// Periodically promotes accounts whose grace period has elapsed from PendingDeletion to
/// PurgeInProgress and kicks off the cross-service purge by publishing AccountPurgeStartedEvent,
/// which Echo's AccountDeletionSaga starts on.
/// </summary>
public class AccountDeletionPurgeSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountDeletionPurgeSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Env.AccountDeletion.SweepInterval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Account deletion purge sweep failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var now = DateTimeOffset.UtcNow;
        var due = await ctx.Users
            .Where(u => u.Status == UserStatus.PendingDeletion && u.PurgeScheduledAt != null && u.PurgeScheduledAt <= now)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var user in due)
            user.BeginPurge();

        await ctx.SaveChangesAsync(ct);

        foreach (var user in due)
        {
            logger.LogInformation("Grace period elapsed for {UserId}, starting purge fan-out", user.Id);
            await bus.PublishAsync(new AccountPurgeStartedEvent { UserId = user.Id });
        }
    }
}
