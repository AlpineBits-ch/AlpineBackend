using AppEnvironment;
using Facet.Extensions;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;

namespace Social.Tests.Dtos;

/// <summary>
/// The avatar and banner URLs on <c>ProfileDto</c>, which ride on every profile projection the REST
/// API returns.
/// </summary>
[TestFixture]
public class ProfileFacetMappingTests
{
    private string _originalInstanceUrl = null!;

    [SetUp]
    public void SetUp() => _originalInstanceUrl = Env.GeneralConfiguration.InstanceUrl;

    [TearDown]
    public void TearDown() => Env.GeneralConfiguration.InstanceUrl = _originalInstanceUrl;

    private static Profile MakeProfile() => new()
    {
        Id = "prfl_7",
        UserId = "user-7",
        UserName = "tester",
    };

    [Test]
    public void ToFacet_BuildsMediaUrlsFromTheConfiguredInstanceUrl()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://selfhosted.example.net";

        var dto = MakeProfile().ToFacet<Profile, ProfileDto>();

        Assert.Multiple(() =>
        {
            Assert.That(dto.AvatarUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_7/avatar"));
            Assert.That(dto.BannerUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_7/banner"));
        });
    }

    [Test]
    public void ToFacet_TrailingSlashOnInstanceUrl_DoesNotDoubleTheSeparator()
    {
        // INSTANCE_URL is operator-written, so a trailing slash is a matter of taste rather than a
        // misconfiguration. https://host//api/v1/... is routed by some proxies and 404'd by others,
        // which would make this break for only some deployments.
        Env.GeneralConfiguration.InstanceUrl = "https://selfhosted.example.net/";

        var dto = MakeProfile().ToFacet<Profile, ProfileDto>();

        Assert.Multiple(() =>
        {
            Assert.That(dto.AvatarUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_7/avatar"));
            Assert.That(dto.BannerUrl, Is.EqualTo("https://selfhosted.example.net/api/v1/social/profiles/prfl_7/banner"));
        });
    }

    [Test]
    public void ToFacet_DefaultInstanceUrl_KeepsTheHostProductionAlreadyServes()
    {
        // The INSTANCE_URL fallback is the exact string this replaced, so venta.gg's payloads are
        // unchanged by the substitution even if the variable were never set.
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";

        var dto = MakeProfile().ToFacet<Profile, ProfileDto>();

        Assert.That(dto.AvatarUrl, Is.EqualTo("https://api.venta.gg/api/v1/social/profiles/prfl_7/avatar"));
    }
}
