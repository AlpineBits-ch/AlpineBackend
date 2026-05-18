using Guild.Application.Dtos.Response;

namespace Guild.Application.Extensions;

public static class GuildExtensions
{
    public static string GetCacheId(this Domain.Aggregates.Guild guild) => $"guild:${guild.Id}";
    public static string GetPresenceCacheId(this Domain.Aggregates.Guild guild) => $"guild:presence:{guild.Id}";
    
    public static string GetCacheId(this GuildDto guild) => $"guild:${guild.Id}";
    public static string GetPresenceCacheId(this GuildDto guild) => $"guild:presence:{guild.Id}";
}