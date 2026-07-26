using Isle.Api.Services.Quests;

namespace Isle.Api.Services.Hosted;

/// <summary>The automatic quest giver's clock.</summary>
public sealed class QuestDirectorService(
    IServiceScopeFactory scopeFactory,
    ILogger<QuestDirectorService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    /// <summary>Let the roster service land its first read before trying to place anything.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

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
            using var scope = scopeFactory.CreateScope();
            var spawner = scope.ServiceProvider.GetRequiredService<QuestSpawner>();
            var director = scope.ServiceProvider.GetRequiredService<QuestDirector>();
            var completion = scope.ServiceProvider.GetRequiredService<QuestCompletionService>();
            var bounties = scope.ServiceProvider.GetRequiredService<BountyService>();

            await completion.ResolveDueQuestsAsync(ct);
            await bounties.ExpireDueBountiesAsync(ct);

            if (await director.ChooseAsync(ct) is { } candidate)
                await spawner.SpawnAsync(candidate, adminSpawned: false, ct);
        }
        catch (Exception ex)
        {
            l