using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Services;

public class UserConversationPermissions
{
    public HashSet<string> ConversationIds { get; set; } = [];
}

public class ConversationPermissionService(MicroserviceContext ctx, IDistributedCache cache)
{

    public  async Task<bool> HasPermission(string userId, string conversationId)
    {
        var permissions = await GetPermissionsForUser(userId);
        if (permissions.ConversationIds.Contains(conversationId)) return true;

        // A cached "not a member" is only ever a hint - the entry is written with a 10 minute TTL
        // and is refreshed by handlers that run off the bus (ConversationCreated,
        // FriendshipAccepted), so it can lag behind the database by however long the
        // outbox/RabbitMQ round-trip takes.
        var isMember = await ctx.Members
            .AnyAsync(m => m.UserId == userId && m.ConversationId == conversationId);

        if (isMember) await GetPermissionsForUser(userId, rebuild: true);

        return isMember;
    }
    
    /// <summary>
    /// Drops a user's cached membership set, for when they stop being a member. Nothing below
    /// re-reads the database while a positive entry stands, so a removal that skips this leaves the
    /// former member reading the conversation until the entry expires.
    /// </summary>
    /// <param name="userId">The user whose membership changed.</param>
    public async Task InvalidateAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        await cache.RemoveAsync(CacheId(userId));
    }

    private static string CacheId(string userId) => "messaging:conversation-permissions:" + userId;

    public  async Task<UserConversationPermissions> GetPermissionsForUser(string userId, bool rebuild = false)

    {
        if (string.IsNullOrEmpty(userId))
        {
            throw new ArgumentException("User ID is required", nameof(userId));
        }

        var cacheId = CacheId(userId);
        
        var cachedData = await cache.GetAsync(cacheId);

        if (cachedData is not null && !rebuild)
        {
            return JsonSerializer.Deserialize<UserConversationPermissions>(Encoding.UTF8.GetString(cachedData))!;
        }
        
        var memberships = await ctx.Members.Where(m => m.UserId == userId).Select(m => m.ConversationId)
                .ToListAsync();
        var permissions = new UserConversationPermissions
        {
            ConversationIds = memberships.ToHashSet()
        };


        await cache.SetAsync(cacheId, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(permissions)), new DistributedCacheEntryOptions()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
        });
        
       
        return permissions;
    }
}