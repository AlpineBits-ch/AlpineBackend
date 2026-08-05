using Social.Api.Services;
using Social.Contracts.Dtos;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>
/// The boundary between an unauthenticated local IPC socket and every server a user is in.
/// </summary>
[TestFixture]
public class ActivityWriteGuardTests
{
    private const string KnownAppId = "356875221078245376";
    private const string UnknownAppId = "111111111111111111";

    private TestSocialContext _context = null!;
    private ActivityWriteGuard _guard = null!;
    private DateTimeOffset _now;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _guard = new ActivityWriteGuard(new GameCatalogLookup(_context));
        _now = DateTimeOffset.UtcNow;

        _context.GameApplications.Add(new GameApplication
        {
            Id = GameApplication.GenerateId(),
            DiscordApplicationId = KnownAppId,
            Name = "Overwatch",
            Source = GameCatalogSource.Seeded,
            IsEnabled = true,
        });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ActivityDto Activity(
        string type = nameof(ActivityType.Playing),
        string source = nameof(ActivitySource.Rpc),
        string? name = "client supplied name",
        string? applicationId = KnownAppId) => new()
    {
        Type = type,
        Source = source,
        Name = name!,
        ApplicationId = applicationId,
    };

    private Task<IReadOnlyList<ActivityDto>> Sanitize(params ActivityDto[] input) =>
        _guard.SanitizeAsync(input, _now);

    // ── The control: names are vouched for, never taken on trust ────────────────────────────

    [Test]
    public async Task Sanitize_KnownApplicationId_ReplacesClientNameWithCanonicalName()
    {
        var result = await Sanitize(Activity(name: "Totally Not A Slur"));

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Overwatch"),
            "the catalog names a recognised application; the caller's string is discarded");
    }

    [Test]
    public async Task Sanitize_UnknownApplicationId_DropsTheActivity()
    {
        var result = await Sanitize(Activity(applicationId: UnknownAppId));

        Assert.That(result, Is.Empty, "an id we do not recognise is one we cannot vouch for");
    }

    [Test]
    public async Task Sanitize_OverLengthApplicationId_IsRejectedNotTruncated()
    {
        // Truncating an id does not produce a harmlessly-wrong id, it produces a different one -
        // and a 21-digit string trimmed to 20 could name some entirely unrelated application.
        var tooLong = KnownAppId + "99";

        Assert.That(await Sanitize(Activity(applicationId: tooLong)), Is.Empty);
    }

    [Test]
    public async Task Sanitize_DisabledApplication_DropsTheActivity()
    {
        _context.GameApplications.Single().IsEnabled = false;
        await _context.SaveChangesAsync();

        Assert.That(await Sanitize(Activity()), Is.Empty);
    }

    [TestCase(nameof(ActivitySource.Rpc))]
    [TestCase(nameof(ActivitySource.ProcessScan))]
    [TestCase(nameof(ActivitySource.Native))]
    public async Task Sanitize_GameShapedSourceWithNoApplicationId_IsDropped(string source)
    {
        var result = await Sanitize(Activity(source: source, applicationId: null, name: "Free Text Game"));

        Assert.That(result, Is.Empty, "a game name with nothing behind it is exactly the forgery this closes");
    }

    [TestCase(nameof(ActivitySource.Manual))]
    [TestCase(nameof(ActivitySource.Media))]
    public async Task Sanitize_FreeTextSourceWithNoApplicationId_IsKept(string source)
    {
        var result = await Sanitize(Activity(source: source, applicationId: null, name: "Bohemian Rhapsody"));

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Bohemian Rhapsody"));
    }

    // ── Text sanitization ───────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sanitize_StripsBidiOverride()
    {
        // U+202E reverses the visual order of everything after it - enough for one status line to
        // impersonate another, and invisible to HTML escaping because it is not markup.
        var hostile = "Playing" + (char)0x202E + "gnihtemos esle";

        var result = await Sanitize(Activity(source: nameof(ActivitySource.Manual), applicationId: null, name: hostile));

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Does.Not.Contain((char)0x202E));
    }

    [Test]
    public async Task Sanitize_CollapsesNewlinesToSpacesRatherThanDeletingThem()
    {
        var value = "line one" + (char)0x0A + "line two";

        var result = await Sanitize(new ActivityDto
        {
            Type = nameof(ActivityType.Playing),
            Source = nameof(ActivitySource.Rpc),
            Name = "ignored",
            ApplicationId = KnownAppId,
            Details = value,
        });

        Assert.That(result[0].Details, Is.EqualTo("line one line two"),
            "deleting the newline would silently weld two words together");
    }

    [Test]
    public async Task Sanitize_StripsNulAndOtherControlCharacters()
    {
        var value = "before" + (char)0x00 + (char)0x07 + "after";

        var result = await Sanitize(new ActivityDto
        {
            Type = nameof(ActivityType.Playing),
            Source = nameof(ActivitySource.Rpc),
            Name = "ignored",
            ApplicationId = KnownAppId,
            State = value,
        });

        Assert.That(result[0].State, Does.Not.Contain((char)0x00));
        Assert.That(result[0].State, Does.Not.Contain((char)0x07));
    }

    [Test]
    public async Task Sanitize_CapsFieldLengths()
    {
        var result = await Sanitize(new ActivityDto
        {
            Type = nameof(ActivityType.Playing),
            Source = nameof(ActivitySource.Rpc),
            Name = "ignored",
            ApplicationId = KnownAppId,
            Details = new string('a', 5_000),
            State = new string('b', 5_000),
        });

        Assert.That(result[0].Details, Has.Length.EqualTo(ActivityLimits.MaxTextLength));
        Assert.That(result[0].State, Has.Length.EqualTo(ActivityLimits.MaxTextLength));
    }

    [Test]
    public async Task Sanitize_WhitespaceOnlyText_BecomesNull()
    {
        var result = await Sanitize(new ActivityDto
        {
            Type = nameof(ActivityType.Playing),
            Source = nameof(ActivitySource.Rpc),
            Name = "ignored",
            ApplicationId = KnownAppId,
            Details = "     ",
        });

        Assert.That(result[0].Details, Is.Null);
    }

    // ── Enum handling ───────────────────────────────────────────────────────────────────────

    [TestCase("Sleeping")]
    [TestCase("")]
    [TestCase(null)]
    public async Task Sanitize_UnknownType_DropsTheActivity(string? type)
    {
        Assert.That(await Sanitize(Activity(type: type!)), Is.Empty);
    }

    [Test]
    public async Task Sanitize_OrdinalInsteadOfEnumName_IsRejected()
    {
        // Enum.TryParse accepts "0" and would silently produce Playing, which nobody sent.
        Assert.That(await Sanitize(Activity(type: "0")), Is.Empty);
    }

    [Test]
    public async Task Sanitize_UnknownSource_DropsTheActivity()
    {
        Assert.That(await Sanitize(Activity(source: "Telepathy")), Is.Empty);
    }

    [Test]
    public async Task Sanitize_TypeIsCaseInsensitiveAndNormalizedToMemberName()
    {
        var result = await Sanitize(Activity(type: "pLaYiNg"));

        Assert.That(result[0].Type, Is.EqualTo(nameof(ActivityType.Playing)));
    }

    // ── Timestamps ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sanitize_FutureStartTime_IsDropped()
    {
        var activity = Activity();
        activity.StartedAt = _now.AddHours(1).ToUnixTimeMilliseconds();

        Assert.That((await Sanitize(activity))[0].StartedAt, Is.Null,
            "a future start renders as a negative elapsed time on every client");
    }

    [Test]
    public async Task Sanitize_ImplausiblyOldStartTime_IsDropped()
    {
        var activity = Activity();
        activity.StartedAt = _now.AddDays(-30).ToUnixTimeMilliseconds();

        Assert.That((await Sanitize(activity))[0].StartedAt, Is.Null);
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    public async Task Sanitize_NonPositiveStartTime_IsDropped(long value)
    {
        var activity = Activity();
        activity.StartedAt = value;

        Assert.That((await Sanitize(activity))[0].StartedAt, Is.Null);
    }

    [Test]
    public async Task Sanitize_RecentStartTime_IsKept()
    {
        var expected = _now.AddMinutes(-23).ToUnixTimeMilliseconds();
        var activity = Activity();
        activity.StartedAt = expected;

        Assert.That((await Sanitize(activity))[0].StartedAt, Is.EqualTo(expected));
    }

    [Test]
    public async Task Sanitize_EndBeforeStart_IsDropped()
    {
        var activity = Activity();
        activity.StartedAt = _now.AddMinutes(-10).ToUnixTimeMilliseconds();
        activity.EndsAt = _now.AddMinutes(-20).ToUnixTimeMilliseconds();

        Assert.That((await Sanitize(activity))[0].EndsAt, Is.Null);
    }

    // ── Party ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sanitize_PartySizeExceedingMax_IsClampedToMax()
    {
        var activity = Activity();
        activity.Party = new ActivityPartyDto { Size = 9, Max = 5 };

        Assert.That((await Sanitize(activity))[0].Party!.Size, Is.EqualTo(5), "'9 of 5' is not a party");
    }

    [Test]
    public async Task Sanitize_NegativePartyNumbers_BecomeNull()
    {
        var activity = Activity();
        activity.Party = new ActivityPartyDto { Size = -3, Max = -1 };

        Assert.That((await Sanitize(activity))[0].Party, Is.Null);
    }

    // ── Assets ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sanitize_AlwaysDropsAssets()
    {
        // Artwork is not sourced yet.
        var activity = Activity();
        activity.Assets = new ActivityAssetsDto { LargeImageUrl = "https://evil.example/track.png" };

        Assert.That((await Sanitize(activity))[0].Assets, Is.Null);
    }

    // ── List handling ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Sanitize_MoreThanTheLimit_TruncatesRatherThanRejecting()
    {
        var many = Enumerable.Range(0, 10).Select(_ => Activity()).ToArray();

        var result = await Sanitize(many);

        Assert.That(result, Has.Count.EqualTo(ActivityLimits.MaxActivities),
            "failing the whole write would clear presence over a recoverable client bug");
    }

    [Test]
    public async Task Sanitize_NullOrEmptyInput_ReturnsEmpty()
    {
        Assert.That(await _guard.SanitizeAsync(null, _now), Is.Empty);
        Assert.That(await _guard.SanitizeAsync([], _now), Is.Empty);
    }

    [Test]
    public async Task Sanitize_MixedGoodAndBad_KeepsOnlyTheGood()
    {
        var result = await Sanitize(
            Activity(applicationId: UnknownAppId),
            Activity(),
            Activity(type: "Sleeping"));

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Overwatch"));
    }
}
