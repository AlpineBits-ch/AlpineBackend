using AppEnvironment;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Federation.Application.Services;

public class UserService(IDistributedCache cache, IMessageBus bus)
{
    public async Task<ProfileDto?> GetUserProfile(string userId)
    {
        var cachedString = await cache.GetStringAsync($"user_profile:{userId}");
        if (cachedString is not null)
        {
            return JsonSerializer.Deserialize<ProfileDto>(cachedString)!;
        }
 
        var response = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = userId
        });
        
        if(response.Profile is not null)
        {
            await cache.SetStringAsync($"user_profile:{userId}", JsonSerializer.Serialize(response.Profile), new DistributedCacheEntryOptions()
            {
                SlidingExpiration = TimeSpan.FromSeconds(30)
            });
            return response.Profile;
        }

        return null;

    }
    
    // Per the canonical-ID convention (see the federation protocol doc), a user keeps their
    // local id everywhere - the <id>:<domain> form only exists for cross-instance routing, built
    // by suffixing this instance's own address.
    public string GetFederatedUserId(string userId) => $"{userId}:{Env.GeneralConfiguration.InstanceUrl}";
}