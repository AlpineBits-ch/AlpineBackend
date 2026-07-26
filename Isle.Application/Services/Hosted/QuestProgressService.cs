using Isle.Api.Services.Quests;
using Isle.Api.Services.World;

namespace Isle.Api.Services.Hosted;

/// <summary>Samples who is standing at an open quest, once per roster refresh.</summary>
public sealed class QuestProgressService(
    IServiceScopeFactory scopeFactory,
    WorldRosterCache roster,
    ILogger<QuestProgressService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>How old the roster may be and still be worth crediting.</summary>
    private static readonly TimeSpan MaxRosterAge = TimeSpan.FromSeconds(60);

    /// <summary>Let the roster service land its first read before crediting anything.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(35);

    /// <summary>The roster timestamp the last credit ran against.</summary>
    private DateTimeOffset? _lastCreditedRoster;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            await SafeTickAsync(ct);

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try
        {
            if (roster.IsStale(MaxRosterAge))
                return;

            if (roster.LastUpdatedAt is not { } updatedAt || updatedAt == _lastCreditedRoster)
                return;

            using var scope = scopeFactory.CreateScope();
            var completion = scope.ServiceProvider.GetRequiredService<QuestCompletionService>();

            await completion.TrackPresenceAsync(ct);

            _lastCreditedRoster = updatedAt;
        }
        catch (Exception ex)
        {
            // A dropped sample costs one tick of credit.
            logger.LogWarning(ex, "Quest presence tick failed");
        }
    }
}
