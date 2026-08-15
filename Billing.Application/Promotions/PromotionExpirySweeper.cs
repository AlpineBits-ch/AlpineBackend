namespace Billing.Application.Promotions;

/// <summary>Stamps <c>ExpiredAt</c> on redemptions whose clock has run out.</summary>
public class PromotionExpirySweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<PromotionExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Bounded so a backlog drains over several passes rather than becoming one long
    /// transaction, matching the other sweeps in this service.</summary>
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var redemptions = scope.ServiceProvider.GetRequiredService<PromotionRedemptionService>();

                var stamped = await redemptions.StampExpiredAsync(BatchSize, stoppingToken);

                if (stamped > 0)
                {
                    logger.LogInformation("Stamped {Count} lapsed promotion redemption(s).", stamped);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Promotion expiry sweep failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
