using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Wolverine;

namespace Social.Contracts.Services;

public class ProfileService(IMessageBus messageBus, IDistributedCache cache)
{
    public async Task<ProfileDto?> GetProfileByUserId(string userId)
    {
        var cachedProfileDto = await cache.GetStringAsync(ProfileDto.GetCacheIdByUserId(userId));
        if (cachedProfileDto != null)
        {
            return JsonSerializer.Deserialize<ProfileDto>(cachedProfileDto);
        }

        var response = await messageBus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest()
        {
            UserId = userId
        });

        if (response.Profile != null)
        {
            await cache.SetStringAsync(ProfileDto.GetCacheIdByUserId(userId), JsonSerializer.Serialize(response.Profile));
        }

        return response?.Profile;
    }

    public async Task<ICollection<ProfileDto>> GetProfilesByUserIds(ICollection<string> userIds)
    {
        // todo ke bock uf das caching shit

        var response = await messageBus.InvokeAsync<GetProfileByUserIdsResponse>(new GetProfileByUserIdsRequest()
        {
            UserIds = userIds
        });
        
        return response.Profiles;

    }
}