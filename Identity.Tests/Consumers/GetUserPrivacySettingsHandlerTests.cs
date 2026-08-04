using Alba;
using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Consumers;

/// <summary>
/// The batched cross-service lookup that Messaging, Social, Guild and Isle resolve policy through.
///
/// <para>Run against the real Postgres context rather than the InMemory provider on purpose: the
/// handler's read is a projection over an <c>IN</c> filter with Npgsql enum columns, and InMemory
/// cannot fail on an untranslatable query - it evaluates everything client-side, so a projection
/// that would throw against Postgres passes there silently.</para>
/// </summary>
[TestFixture]
public class GetUserPrivacySettingsHandlerTests
{
    private static IAlbaHost Host => AppFixture.Host;

    private IServiceScope _scope = null!;
    private MicroserviceContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = Host.Services.CreateScope();
        _ctx = _scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (await _ctx.Users.AnyAsync())
        {
            _ctx.Users.RemoveRange(_ctx.Users);
            await _ctx.SaveChangesAsync();
        }
        _scope.Dispose();
    }

    private async Task<string> SeedUserAsync(Action<UserPrivacySettings>? configure = null)
    {
        var user = Identity.Domain.Aggregates.ApplicationUser.Create(new Identity.Domain.Aggregates.CreateUserParams
        {
            Email = $"pv-{Guid.NewGuid():N}@example.com",
            PhoneNumber = $"+4179{Random.Shared.Next(1000000, 9999999)}",
            Username = $"pv{Guid.NewGuid():N}"[..15],
            BirthDate = new DateOnly(2000, 1, 1),
        });

        configure?.Invoke(user.UserPrivacySettings!);

        _ctx.Users.Add(user);
        await _ctx.SaveChangesAsync();
        return user.Id;
    }

    private Task<GetUserPrivacySettingsResponse> Get(params string[] userIds) =>
        GetUserPrivacySettingsHandler.Handle(
            new GetUserPrivacySettingsRequest { UserIds = userIds.ToList() }, _ctx);

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ReturnsTheStoredRecord()
    {
        var userId = await SeedUserAsync(s =>
        {
            s.DirectMessagePolicy = DirectMessagePolicy.Nobody;
            s.AllowDataCollection = true;
            s.DmRetentionDays = 14;
            s.ExplicitContentFilter = ExplicitContentFilter.Everyone;
            s.Version = 3;
        });

        var response = await Get(userId);
        var settings = response.Settings.Single();

        Assert.Multiple(() =>
        {
            Assert.That(settings.UserId, Is.EqualTo(userId));
            Assert.That(settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Nobody));
            Assert.That(settings.AllowDataCollection, Is.True);
            Assert.That(settings.DmRetentionDays, Is.EqualTo(14));
            Assert.That(settings.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.Everyone));
            Assert.That(settings.Version, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Handle_AnswersEveryIdInOneRoundTrip()
    {
        var first = await SeedUserAsync(s => s.DirectMessagePolicy = DirectMessagePolicy.Everyone);
        var second = await SeedUserAsync(s => s.DirectMessagePolicy = DirectMessagePolicy.Nobody);

        var response = await Get(first, second);

        Assert.That(response.Settings.Select(s => s.UserId), Is.EquivalentTo(new[] { first, second }));
        Assert.That(response.Settings.Single(s => s.UserId == first).DirectMessagePolicy,
            Is.EqualTo(DirectMessagePolicy.Everyone));
        Assert.That(response.Settings.Single(s => s.UserId == second).DirectMessagePolicy,
            Is.EqualTo(DirectMessagePolicy.Nobody));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_EmptyRequest_ReturnsNothingRatherThanEverything()
    {
        await SeedUserAsync();

        var response = await Get();

        Assert.That(response.Settings, Is.Empty);
    }

    [Test]
    public async Task Handle_DuplicateIds_AreAnsweredOnce()
    {
        var userId = await SeedUserAsync();

        var response = await Get(userId, userId, userId);

        Assert.That(response.Settings, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Handle_BlankIds_AreIgnored()
    {
        var userId = await SeedUserAsync();

        var response = await Get(userId, "", "   ");

        Assert.That(response.Settings, Has.Count.EqualTo(1));
        Assert.That(response.Settings.Single().UserId, Is.EqualTo(userId));
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_UnknownUser_GetsTheRestrictiveDefaultsRatherThanNoAnswer()
    {
        // The failure this prevents: a caller that receives nothing for an id has to invent a
        // fallback, and the fallback invented under time pressure is "no restriction". So the handler
        // answers, and it answers closed.
        var response = await Get("user_doesnotexist");

        var settings = response.Settings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(settings.UserId, Is.EqualTo("user_doesnotexist"));
            Assert.That(settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
            Assert.That(settings.DirectMessagePolicy, Is.Not.EqualTo(DirectMessagePolicy.Everyone));
            Assert.That(settings.AllowDataCollection, Is.False);
            Assert.That(settings.AllowPersonalization, Is.False);
            Assert.That(settings.AllowVoiceRecordingInClips, Is.False);
            Assert.That(settings.DiscoverableByEmail, Is.False);
            Assert.That(settings.DiscoverableByPhone, Is.False);
            Assert.That(settings.BirthdayVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(settings.Version, Is.Zero);
        });
    }

    [Test]
    public async Task Handle_MixOfKnownAndUnknownIds_AnswersBoth()
    {
        var known = await SeedUserAsync(s => s.DirectMessagePolicy = DirectMessagePolicy.Everyone);

        var response = await Get(known, "user_ghost");

        Assert.That(response.Settings, Has.Count.EqualTo(2));
        Assert.That(response.Settings.Single(s => s.UserId == known).DirectMessagePolicy,
            Is.EqualTo(DirectMessagePolicy.Everyone));
        Assert.That(response.Settings.Single(s => s.UserId == "user_ghost").DirectMessagePolicy,
            Is.EqualTo(DirectMessagePolicy.Friends),
            "an unknown id must never come back more permissive than a real one's default");
    }

    [Test]
    public async Task Handle_DoesNotTrackTheRowsItReturns()
    {
        // The read runs on the DM send path. Materializing tracked entities there would put the
        // settings row in a change tracker this handler has no business writing through - and
        // Wolverine's EF middleware commits that tracker on the way out.
        var userId = await SeedUserAsync();
        _ctx.ChangeTracker.Clear();

        await Get(userId);

        Assert.That(_ctx.ChangeTracker.Entries<UserPrivacySettings>(), Is.Empty);
    }
}
