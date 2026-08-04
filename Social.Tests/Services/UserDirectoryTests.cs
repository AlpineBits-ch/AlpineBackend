using Identity.Contracts.Bus.Response;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>The discoverability chokepoint (privacy spec T2-16).</summary>
[TestFixture]
public class UserDirectoryTests
{
    private TestSocialContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task AddProfile(string userId, string userName)
    {
        _context.Profiles.Add(Profile.Create(new CreateProfileParams { UserId = userId, Username = userName }));
        await _context.SaveChangesAsync();
    }

    private UserDirectory DirectoryFor(params UserPrivacySettingsSummary[] settings) =>
        PrivacyTestHelpers.Directory(_context, PrivacyTestHelpers.CacheReturning(_cache, _bus, settings));

    private UserDirectory FailingDirectory() =>
        PrivacyTestHelpers.Directory(_context, PrivacyTestHelpers.FailingCache(_cache, _bus));

    // ── the flag table ───────────────────────────────────────────────────────

    [Test]
    public void IsDiscoverableBy_ReadsTheFlagThatGovernsEachKey()
    {
        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.DiscoverableByUsername = true;
        settings.DiscoverableByEmail = false;
        settings.DiscoverableByPhone = false;

        Assert.Multiple(() =>
        {
            Assert.That(UserDirectory.IsDiscoverableBy(settings, DirectoryKey.Username), Is.True);
            Assert.That(UserDirectory.IsDiscoverableBy(settings, DirectoryKey.Email), Is.False);
            Assert.That(UserDirectory.IsDiscoverableBy(settings, DirectoryKey.Phone), Is.False);
        });

        settings.DiscoverableByUsername = false;
        settings.DiscoverableByEmail = true;
        settings.DiscoverableByPhone = true;

        Assert.Multiple(() =>
        {
            Assert.That(UserDirectory.IsDiscoverableBy(settings, DirectoryKey.Username), Is.False);
            Assert.That(UserDirectory.IsDiscoverableBy(settings, DirectoryKey.Email), Is.True,
                "DiscoverableByEmail must be the flag an email lookup consults, not a stored value nothing reads");
            Assert.That(UserDirectory.IsDiscoverableBy(settings, DirectoryKey.Phone), Is.True);
        });
    }

    [Test]
    public void IsDiscoverableBy_EveryKeyIsRefusedWhenItsOwnFlagIsOff()
    {
        // The shipped defaults for email and phone are both false, and the restrictive defaults set
        // all three false. Whichever key a future lookup uses, "off" must mean "not found".
        var closed = PrivacySettingsCache.RestrictiveDefaults("user-1");

        foreach (var key in Enum.GetValues<DirectoryKey>())
            Assert.That(UserDirectory.IsDiscoverableBy(closed, key), Is.False, $"{key} must fail closed");
    }

    [Test]
    public void IsDiscoverableBy_UnknownKey_FailsClosed()
    {
        // A key added to the enum without a flag to govern it is undiscoverable until somebody says
        // which flag governs it - the opposite default would silently ship an ungated lookup.
        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.DiscoverableByUsername = true;
        settings.DiscoverableByEmail = true;
        settings.DiscoverableByPhone = true;

        Assert.That(UserDirectory.IsDiscoverableBy(settings, (DirectoryKey)99), Is.False);
    }

    // ── username: the only key that resolves ─────────────────────────────────

    [Test]
    public async Task FindAsync_Username_ReturnsTheSubjectAndTheirSettings()
    {
        await AddProfile("user-1", "target");
        var settings = PrivacyTestHelpers.Defaults("user-1");

        var match = await DirectoryFor(settings).FindAsync(DirectoryKey.Username, "target");

        Assert.That(match, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(match!.Profile.UserId, Is.EqualTo("user-1"));
            Assert.That(match.Settings.UserId, Is.EqualTo("user-1"),
                "the match carries the record it was admitted by, so the caller needs no second lookup");
        });
    }

    [Test]
    public async Task FindAsync_Username_NotDiscoverable_IsIndistinguishableFromNoSuchUser()
    {
        await AddProfile("user-1", "target");
        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.DiscoverableByUsername = false;

        var hidden = await DirectoryFor(settings).FindAsync(DirectoryKey.Username, "target");
        var absent = await DirectoryFor(settings).FindAsync(DirectoryKey.Username, "nobody-holds-this");

        Assert.Multiple(() =>
        {
            Assert.That(hidden, Is.Null);
            Assert.That(absent, Is.Null);
            Assert.That(hidden, Is.EqualTo(absent),
                "a hidden user and a non-existent one must produce the same answer, or the difference is an oracle");
        });
    }

    [Test]
    public async Task FindAsync_PrivacyLookupFailure_FindsNobody()
    {
        // Identity unreachable: the restrictive defaults set every Discoverable* flag false, so an
        // outage makes everyone undiscoverable rather than everyone discoverable.
        await AddProfile("user-1", "target");

        var match = await FailingDirectory().FindAsync(DirectoryKey.Username, "target");

        Assert.That(match, Is.Null);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task FindAsync_BlankValue_ResolvesNothing(string value)
    {
        await AddProfile("user-1", "target");

        var match = await DirectoryFor(PrivacyTestHelpers.Defaults("user-1"))
            .FindAsync(DirectoryKey.Username, value);

        Assert.That(match, Is.Null);
    }

    // ── email and phone: no resolver exists ──────────────────────────────────

    [TestCase(DirectoryKey.Email)]
    [TestCase(DirectoryKey.Phone)]
    public async Task FindAsync_EmailAndPhone_ResolveNothingBecauseNoSuchLookupExists(DirectoryKey key)
    {
        // This is the executable form of the T2-16 finding: Social's Profile stores neither an
        // email nor a phone number, and no service in the solution offers a lookup by either.
        await AddProfile("user-1", "target");

        var settings = PrivacyTestHelpers.Defaults("user-1");
        settings.DiscoverableByEmail = true;
        settings.DiscoverableByPhone = true;

        var match = await DirectoryFor(settings).FindAsync(key, "target@example.com");

        Assert.That(match, Is.Null, "even with the flag switched on there is nothing to resolve");
    }

    [Test]
    public async Task FindAsync_UnknownKey_ResolvesNothing()
    {
        await AddProfile("user-1", "target");

        var match = await DirectoryFor(PrivacyTestHelpers.Defaults("user-1"))
            .FindAsync((DirectoryKey)99, "target");

        Assert.That(match, Is.Null);
    }
}
