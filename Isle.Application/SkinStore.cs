using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api;

public class SkinStore(IServiceScopeFactory scopeFactory) : ISkinStore
{
    public async Task<SkinCustomizer?> GetAsync(string steam, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var player = context.Players.AsNoTracking().Include(player => player.Skins).FirstOrDefault(x => x.SteamId == steam);
        if (player == null)
            return null;
        
        return player.Skins.LastOrDefault()?.Customizer;
    }
}