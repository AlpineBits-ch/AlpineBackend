using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Api.Extensions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Relationship.Consumers;

public class GetProfilesByUserIdsHandler
{
    public static  async Task<GetProfileByUserIdsResponse>Handle(GetProfileByUserIdsRequest request, ILogger<GetProfileByUserIdHandler> logger, MicroserviceContext ctx, IDistributedCache cache)
    {
        
        
        var cachedProfiles = new List<ProfileDto>();


        foreach (var requestUserId in request.UserIds)
        {
            var data = await cache.GetStringAsync(ProfileDto.GetCacheIdByUserId(requestUserId));
            if (data is not null)
            {
                var deserialized = JsonSerializer.Deserialize<ProfileDto>(data);
                if(deserialized is not null) cachedProfiles.Add(deserialized);
            }
        }
        
        var remainingUserIds = request.UserIds.Except(cachedProfiles.Select(p => p.UserId));
        
        var remainingProfiles = await ctx.Profiles
            .Include(p => p.Relationships)
            .ThenInclude(p => p.Target).Where(p => remainingUserIds.Contains(p.UserId)).ToListAsync();
        
        cachedProfiles.AddRange(remainingProfiles.Select(p => p.ToIntegrationProfile()));


        foreach (var p in cachedProfiles)
        {
            await cache.SetStringAsync(ProfileDto.GetCacheIdByUserId(p.UserId), JsonSerializer.Serialize(p));
        }
        
        return new GetProfileByUserIdsResponse()
        {
            Profiles = cachedProfiles
        };
        
    }
}

