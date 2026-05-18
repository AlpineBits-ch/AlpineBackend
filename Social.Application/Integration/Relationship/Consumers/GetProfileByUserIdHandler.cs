using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Social.Infrastructure.Persistence;
using Social.Api.Extensions;
namespace Social.Api.Integration.Relationship.Consumers;

public class GetProfileByUserIdHandler
{
    public static  async Task<GetProfileByUserIdResponse>Handle(GetProfileByUserIdRequest request, ILogger<GetProfileByUserIdHandler> logger, MicroserviceContext ctx, IDistributedCache cache)
    {
        
        var cacheKey = $"integration_profile:user_id:{request.UserId}";
        
        var cacheValue = await cache.GetAsync(cacheKey);
        
        if(cacheValue is not null) 
        {
            
            var cachedProfile = JsonSerializer.Deserialize<ProfileDto>(cacheValue);

            return new GetProfileByUserIdResponse()
            {
                Profile = cachedProfile
            };
        }
        
        var profile = await ctx.Profiles
            .Include(p => p.Relationships)
            .ThenInclude(r => r.Target)
            .FirstOrDefaultAsync(p => p.UserId == request.UserId);
        
        if(profile is null) return new GetProfileByUserIdResponse();
        
        
        var integrationProfile = profile.ToIntegrationProfile();
      
        
        await cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(integrationProfile), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        });

        return new GetProfileByUserIdResponse()
        {
            Profile = integrationProfile
        };
        
    }
}