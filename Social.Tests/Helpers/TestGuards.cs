using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Services;
using Social.Infrastructure.Persistence;

namespace Social.Tests.Helpers;

/// <summary>Construction of the activity write path for tests that are not about it.</summary>
internal static class TestGuards
{
    /// <summary>An <see cref="ActivityWriteGuard"/> whose registry lookup always misses.</summary>
    public static ActivityWriteGuard OfflineActivityGuard(MicroserviceContext context) =>
        new(
            new GameCatalogLookup(context),
            new ApplicationRegistryResolver(
                context,
                StubHttpClientFactory.NotFound(),
                new FakeDistributedCache(),
                NullLogger<ApplicationRegistryResolver>.Instance));
}
