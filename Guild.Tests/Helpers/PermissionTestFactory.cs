using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Helpers;

/// <summary>
/// Builds <see cref="GuildPermissionService"/> with the scene visibility cache it requires, which
/// eighty-odd fixtures would otherwise spell out identically.
/// </summary>
internal static class PermissionTestFactory
{
    /// <summary>The permission resolver, as every fixture wants it.</summary>
    /// <param name="cache">The distributed cache the fixture is using.</param>
    /// <param name="context">The Guild database.</param>
    /// <param name="planFeatures">A plan source, for the fixtures that test clamping.</param>
    /// <param name="sceneVisibility">Pass the fixture's own instance when it invalidates the map;
    /// a cache built here answers for the same keys but memoizes separately.</param>
    /// <returns>The service.</returns>
    public static GuildPermissionService Create(
        IDistributedCache cache,
        MicroserviceContext context,
        IGuildPlanFeatures? planFeatures = null,
        SceneVisibilityCache? sceneVisibility = null) =>
        new(cache, context, NullLogger<GuildPermissionService>.Instance,
            sceneVisibility ?? new SceneVisibilityCache(cache, context, new PersonaService(cache, context)),
            planFeatures);
}
