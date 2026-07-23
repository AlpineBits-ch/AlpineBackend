using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;

namespace Isle.Api;

public class SkinStore(MicroserviceContext context) : ISkinStore
{
    public Task<SkinCustomizer?> GetAsync(string steam, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}