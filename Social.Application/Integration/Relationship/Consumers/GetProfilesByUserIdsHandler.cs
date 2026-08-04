using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Api.Extensions;
using Social.Api.Services;
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

        // Privacy spec T0-3/T2-17. Applied after the cache write for the same reason as the
        // single-profile handler: the cached entry is the unfiltered subject view, and only the
        // outbound copy is narrowed to what this particular viewer may see. One query resolves the
        // whole batch, so a member list does not turn into N block lookups.
        var projected = await ApplyBlocksAsync(ctx, cachedProfiles, request.ViewerUserId);

        return new GetProfileByUserIdsResponse()
        {
            Profiles = projected
        };

    }

    internal static async Task<List<ProfileDto>> ApplyBlocksAsync(
        MicroserviceContext ctx, List<ProfileDto> profiles, string? viewerUserId)
    {
        if (string.IsNullOrWhiteSpace(viewerUserId) || profiles.Count == 0) return profiles;

        var viewerProfileId = await ctx.Profiles.AsNoTracking()
            .Where(p => p.UserId == viewerUserId)
            .Select(p => p.Id)
            .FirstOrDefaultAsync();

        if (viewerProfileId is null) return profiles;

        var blocked = await ctx.BlockedEitherWayAsync(
            viewerProfileId, profiles.Select(p => p.Id).ToList());

        if (blocked.Count == 0) return profiles;

        return profiles
            .Select(p => blocked.Contains(p.Id) ? p.ToMinimalIntegrationProfile() : p)
            .ToList();
    }
}

