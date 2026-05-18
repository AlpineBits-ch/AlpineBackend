using System.Text.Json;
using Messaging.Domain.Entities;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Services;

public class CallService
{
    public static async Task<Call?> GetCallById(string id, IDistributedCache cache)
    {
        var serializedCall = await cache.GetStringAsync(Call.GetCacheId(id));
        if (string.IsNullOrWhiteSpace(serializedCall))
        {
            return null;
        }
        
        var call = JsonSerializer.Deserialize<Call>(serializedCall);
        if (call == null)
        {
            return null;
        }
        
        return call;    
    }
}