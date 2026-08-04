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
    //
    // All five are nullable and omitted from the payload entirely when the viewer may not see
    // them - the spec is explicit that filtering happens in the projection, so "you are not
    // allowed to see this" must not be expressible as a present-but-empty key a client could
    // read the setting off. They are new keys, so a v1 client that never asked for them is
    // unaffected.
    //
    // Populated by ProfileVisibility.Project from a ProfileSupplements bundle; see that type for
    // which of them Social can source today and which are enforcement points waiting on their
    // owning service.

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
    public ProfileActivityDto? Activity { get; set; }
}