using AppEnvironment;
using Identity.Infrastructure.Persistence;

namespace Identity.Application.Services;

/// <summary>
/// Schedules <see cref="RetentionSweep"/> (T1-8), modelled on <see
/// cref="AccountDeletionPurgeSweepService"/>.
/// </summary>
public class RetentionSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Env.Retention.SweepInterval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

                var result = await RetentionSweep.RunAsync(ctx, DateTimeOffset.UtcNow, stoppingToken);

                if (result.Total > 0)
                {
                    logger.LogInformation(
                        "Retention sweep: scrubbed {Sessions} login session(s), {Audit} audit event IP(s), "
                        + "deleted {Deleted} revoked session(s)",
                        result.LoginSessionsScrubbed, result.AuditEventIpsScrubbed,
                        result.RevokedLoginSessionsDeleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention sweep failed");
            }
        }
    }
}
