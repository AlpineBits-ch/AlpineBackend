using System.Text.Json.Serialization;
using Facet;
using Facet.Mapping;
using Social.Domain.Aggregate;

namespace Social.Api.Dtos.Response;

public class ProfileMapConfig : IFacetMapConfiguration<Profile, ProfileDto>
{
    public static void Map(Profile source, ProfileDto target)
    {
        target.AvatarUrl = $"https://api.venta.gg/api/v1/social/profiles/{source.Id}/avatar";
        target.BannerUrl = $"https://api.venta.gg/api/v1/social/profiles/{source.Id}/banner";
    }
}
[Facet(typeof(Profile), NestedFacets = [typeof(NestedRelationshipDto)], MaxDepth = 1, Configuration = typeof(ProfileMapConfig))]
public partial class ProfileDto
{
    public string AvatarUrl { get; set; }
    public string BannerUrl { get; set; }

    // ── Privacy-gated fields (spec T2-17 / T2-19) ────────────────────────────

    /// <summary>Gated by <c>MutualFriendsVisibility</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MutualFriendDto>? MutualFriends { get; set; }

    /// <summary>Gated by <c>MutualServersVisibility</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MutualServerDto>? MutualServers { get; set; }

    /// <summary>Gated by <c>ConnectionsVisibility</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProfileConnectionDto>? Connections { get; set; }

    /// <summary>Gated by <c>BirthdayVisibility</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? Birthday { get; set; }

    /// <summary>Gated by <c>ShareActivity</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Social.Contracts.Dtos.ActivityDto>? Activities { get; set; }
}