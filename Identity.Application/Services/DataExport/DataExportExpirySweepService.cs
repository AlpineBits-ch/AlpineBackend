using AppEnvironment;
using Identity.Infrastructure.Persistence;

namespace Identity.Application.Services.DataExport;

/// <summary>
/// Schedules <see cref="DataExportExpirySweep"/>, modelled on
/// <c>Identity.Application.Services.AccountDeletionPurgeSweepService</c>: a plain loop with its own
/// scope per tick, one try/catch so a bad tick logs and the loop survives, and no transactional
/// middleware wrapped around it.
/// </summary>
public class DataExportExpirySweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<DataExportExpirySweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Env.DataExport.SweepInterval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
                var store = scope.ServiceProvider.GetRequiredService<IDataExportArtifactStore>();

                var result = await DataExportExpirySweep.RunAsync(ctx, store, DateTimeOffset.UtcNow, stoppingToken);

                if (result.RowsExpired > 0)
                {
                    logger.LogInformation(
                        "Data export expiry: deleted {Artifacts} artifact(s), expired {Rows} request(s)",
                        result.ArtifactsDeleted, result.RowsExpired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Data export expiry sweep failed");
            }
        }
    }
}
