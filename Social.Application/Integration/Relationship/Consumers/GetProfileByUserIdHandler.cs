using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Social.Infrastructure.Persistence;
using Social.Api.Extensions;
using Social.Api.Services;
namespace Social.Api.Integration.Relationship.Consumers;

public class GetProfileByUserIdHandler
{
    public static  async Task<GetProfileByUserIdResponse>Handle(GetProfileByUserIdRequest request, ILogger<GetProfileByUserIdHandler> logger, MicroserviceContext ctx, IDistributedCache cache)
    {

        var cacheKey = $"integration_profile:user_id:{request.UserId}";

        var cacheValue = await cache.GetAsync(cacheKey);

        ProfileDto? integrationProfile;

        if(cacheValue is not null)
        {
            integrationProfile = JsonSerializer.Deserialize<ProfileDto>(cacheValue);
        }
        else
        {
            var profile = await ctx.Profiles
                .Include(p => p.Relationships)
                .ThenInclude(r => r.Target)
                .FirstOrDefaultAsync(p => p.UserId == request.UserId);

            if (profile is null) return new GetProfileByUserIdResponse();

            integrationProfile = profile.ToIntegrationProfile();

            await cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(integrationProfile), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            });
        }

        if (integrationProfile is null) return new GetProfileByUserIdResponse();

        // Privacy spec T0-3/T2-17. The cached entry is deliberately viewer-independent - one entry
        // per subject, not per (subject, viewer) pair - so the block filter is applied *after* the
        // cache, on the way out. Caching the filtered form instead would let whichever viewer
        // happened to warm the entry decide what everybody else sees.
        integrationProfile = await ApplyBlockAsync(ctx, integrationProfile, request.ViewerUserId, request.UserId);

        return new GetProfileByUserIdResponse()
        {
            Profile = integrationProfile
        };

    }

    /// <summary>
    /// A viewer on either side of a block gets the minimal projection - the same one the REST route
    /// gives them - so a service asking Social on that viewer's behalf cannot hand them more than
    /// they could fetch themselves.
    /// </summary>
    internal static async Task<ProfileDto> ApplyBlockAsync(
        MicroserviceContext ctx, ProfileDto profile, string? viewerUserId, string subjectUserId)
    {
        if (string.IsNullOrWhiteSpace(viewerUserId) || viewerUserId == subjectUserId) return profile;

        return await ctx.AnyBlockBetweenUsersAsync(viewerUserId, subjectUserId)
            ? profile.ToMinimalIntegrationProfile()
            : profile;
    }
}
