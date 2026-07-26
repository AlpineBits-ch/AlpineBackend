using Isle.Api.Services.Quests;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.State;

/// <summary>
/// What a player should look like right now. The SDK's skin-reapply service asks this on every join
/// and at boot, so it is also the enforcement point for the bounty marker: while a player is marked,
/// their "own" skin <i>is</i> the marker, and reconnecting or respawning cannot shed it. The moment
/// the bounty closes the mark is gone from the registry and this falls back to their real skin.
/// </summary>
public class SkinStore(IServiceScopeFactory scopeFactory, BountyRegistry bounties) : ISkinStore
{
    public async Task<SkinCustomizer?> GetAsync(string steam, CancellationToken ct = default)
    {
        if (await bounties.GetBySteamAsync(steam) is { } mark)
            return BountyMarkerSkin.For(mark.Species);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var player = await context.Players
            .AsNoTracking()
            .Include(p => p.Skins)
            .FirstOrDefaultAsync(x => x.SteamId == steam, ct);

        return player?.Skins.LastOrDefault()?.Customizer;
    }
}
