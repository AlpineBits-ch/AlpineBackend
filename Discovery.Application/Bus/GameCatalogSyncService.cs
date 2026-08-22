using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Wolverine;

namespace Discovery.Api.Bus;

public static class GameCatalogSync
{
    /// <summary>
    /// Mutates the tracked context and returns without saving. The event handler that calls this
    /// runs inside Wolverine's transactional middleware, which commits on a successful return; a
    /// save here would double-commit. The hosted service below owns its own commit instead.
    /// </summary>
    public static async Task RunAsync(MicroserviceContext ctx, IMessageBus bus, CancellationToken ct)
    {
        var existing = await ctx.GameTopics.ToDictionaryAsync(g => g.GameApplicationId, ct);
        var seen = new HashSet<string>();

        string? cursor = null;
        do
        {
            var page = await bus.InvokeAsync<ListGameTopicsResponse>(
                new ListGameTopicsRequest { After = cursor }, ct);

            foreach (var dto in page.Topics)
            {
                seen.Add(dto.Id);
                if (!existing.TryGetValue(dto.Id, out var row))
                {
                    row = new GameTopic { Id = GameTopic.GenerateId(), GameApplicationId = dto.Id };
                    ctx.GameTopics.Add(row);
                    existing[dto.Id] = row;
                }

                row.Name = dto.Name;
                row.Aliases = dto.Aliases;
                row.SteamAppId = dto.SteamAppId;
                row.IsEnabled = dto.IsEnabled;
            }

            cursor = page.NextCursor;
        } while (cursor is not null);

        // Disabled, not deleted: a listing already tagged with a game that left the catalogue must
        // keep rendering its chip.
        foreach (var row in existing.Values.Where(r => !seen.Contains(r.GameApplicationId)))
            row.IsEnabled = false;
    }
}

/// <summary>Syncs at startup and daily. The event handler covers everything in between.</summary>
public class GameCatalogSyncService(IServiceProvider services, ILogger<GameCatalogSyncService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
                await GameCatalogSync.RunAsync(
                    ctx,
                    scope.ServiceProvider.GetRequiredService<IMessageBus>(),
                    stoppingToken);
                await ctx.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Game catalog sync failed, retrying on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
