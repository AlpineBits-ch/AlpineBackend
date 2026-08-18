using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Guild.Tests.Helpers;

/// <summary>
/// Builds the roleplay hub publisher, which every persona, scene and dice route now takes. Its
/// dependency list is long and identical everywhere, so it is assembled here rather than in six
/// SetUps. Pass a presence-bearing multiplexer to make the guild-wide broadcasts reach anybody.
/// </summary>
internal static class RoleplayTestFactory
{
    public static RoleplayRealtimeService CreateRealtime(
        MicroserviceContext context,
        GuildPermissionService permissions,
        PersonaService personas,
        FakeHubContext hub,
        IConnectionMultiplexer? redis = null) =>
        new(context,
            new GuildHydrateService(
                redis ?? RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance),
            new ChannelAudienceService(permissions, new MemoryCache(new MemoryCacheOptions())),
            new PersonaMentionService(context, personas),
            new ModulePermissionHolderService(context, permissions),
            hub);
}
