using Social.Api.Integration.Status;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class UserInactiveHandlerTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_ExistingProfile_SetsOfflineAndUpdatesLastSeen()
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = "user-1", Username = "tester" });
        profile.OnlineStatus = OnlineStatus.Online;
        profile.LastSeenAt = DateTimeOffset.UtcNow.AddHours(-1);
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;
        await UserInactiveHandler.Handle(new UserInactiveEvent { UserId = "user-1" }, _context);

        var stored = _context.Profiles.Single(p => p.UserId == "user-1");
        Assert.Multiple(() =>
        {
            Assert.That(stored.OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
            Assert.That(stored.LastSeenAt, Is.GreaterThanOrEqualTo(before));
        });
    }

    [Test]
    public void Handle_UnknownUser_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() =>
            UserInactiveHandler.Handle(new UserInactiveEvent { UserId = "no-such-user" }, _context));
    }
}
