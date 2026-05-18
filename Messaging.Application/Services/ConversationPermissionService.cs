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
        return permissions.ConversationIds.Contains(conversationId);
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