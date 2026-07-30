using Social.Domain.Aggregate;
using Social.Domain.Enums;

namespace Social.Tests.Domain;

[TestFixture]
public class ProfileTests
{
    [Test]
    public void Create_SetsIdUserIdAndUserName()
    {
        var profile = Profile.Create(new CreateProfileParams
        {
            UserId = "user-1",
            Username = "tester",
        });

        Assert.Multiple(() =>
        {
            Assert.That(profile.Id, Is.Not.Null.And.Not.Empty);
            Assert.That(profile.Id, Does.StartWith("prfl_"));
            Assert.That(profile.UserId, Is.EqualTo("user-1"));
            Assert.That(profile.UserName, Is.EqualTo("tester"));
        });
    }

    [Test]
    public void Create_DoesNotCopyBioFromParams()
    {
        // CreateProfileParams.Bio exists but Profile.Create never assigns it - documents the
        // current behavior (bio must be set separately via UpdateProfileAsync).
        var profile = Profile.Create(new CreateProfileParams
        {
            UserId = "user-1",
            Username = "tester",
            Bio = "hello world",
        });

        Assert.That(profile.Bio, Is.Null);
    }

    [Test]
    public void Create_DefaultsToOfflineAndDefaultFont()
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = "u", Username = "n" });

        Assert.Multiple(() =>
        {
            Assert.That(profile.OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
            Assert.That(profile.Font, Is.EqualTo(ProfileFont.Default));
        });
    }

    [Test]
    public void Create_GeneratesUniqueIdsAcrossCalls()
    {
        var first = Profile.Create(new CreateProfileParams { UserId = "u1", Username = "n1" });
        var second = Profile.Create(new CreateProfileParams { UserId = "u2", Username = "n2" });

        Assert.That(first.Id, Is.Not.EqualTo(second.Id));
    }

    [Test]
    public void GetCacheKey_Static_MatchesInstanceMethod()
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = "u", Username = "n" });

        Assert.That(profile.GetCacheKey(), Is.EqualTo(Profile.GetCacheKey(profile.Id)));
        Assert.That(profile.GetCacheKey(), Is.EqualTo($"profile:{profile.Id}"));
    }
}
