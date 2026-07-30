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
        // and is refreshed by handlers that run off the bus (ConversationCreated, FriendshipAccepted),
        // so it can lag behind the database by however long the outbox/RabbitMQ round-trip takes.
        // Accepting a friend request caches an empty set for both users; if they then open a DM,
        // every permission check would deny them until the ConversationCreated event lands.
        // Confirm a negative against the database before acting on it, and warm the cache back up
        // when it turns out to be stale.
        var isMember = await ctx.Members
            .AnyAsync(m => m.UserId == userId && m.ConversationId == conversationId);

        if (isMember) await GetPermissionsForUser(userId, rebuild: true);

        return isMember;
    }
    
    public  async Task<UserConversationPermissions> GetPermissionsForUser(string userId, bool rebuild = false)

    {
        var cacheId = "messaging:conversation-permissions:" + userId;
        if (string.IsNullOrEmpty(userId))
        {
            throw new ArgumentException("User ID is required", nameof(userId));
        }
        
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