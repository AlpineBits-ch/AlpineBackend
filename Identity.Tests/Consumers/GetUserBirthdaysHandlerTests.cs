using Alba;
using Domain;
using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Consumers;

/// <summary>
/// The batched birthday lookup behind the <c>birthday</c> profile field (privacy spec T2-17).
///
/// <para>Run against the real Postgres context rather than InMemory for the same reason as
/// <see cref="GetUserPrivacySettingsHandlerTests"/>: the read left-joins an owned value object and a
/// 1:1 related entity with Npgsql enum columns, and InMemory cannot fail on an untranslatable query.
/// </para>
///
/// <para>The negative cases are the point. A date of birth is the single most re-identifying field on
/// the account, so what has to hold is that the default answer is "nothing" and that every kind of
/// "no" looks the same.</para>
/// </summary>
[TestFixture]
public class GetUserBirthdaysHandlerTests
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

    private async Task<string> SeedUserAsync(
        DateOnly birthDate,
        Visibility visibility = Visibility.Everyone,
        Action<ApplicationUser>? configure = null)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"bd-{Guid.NewGuid():N}@example.com",
            PhoneNumber = $"+4179{Random.Shared.Next(1000000, 9999999)}",
            Username = $"bd{Guid.NewGuid():N}"[..15],
            BirthDate = birthDate,
        });

        user.UserPrivacySettings!.BirthdayVisibility = visibility;
        configure?.Invoke(user);

        _ctx.Users.Add(user);
        await _ctx.SaveChangesAsync();
        return user.Id;
    }

    private Task<GetUserBirthdaysResponse> Get(params string[] userIds) =>
        GetUserBirthdaysHandler.Handle(new GetUserBirthdaysRequest { UserIds = userIds.ToList() }, _ctx);

    private static DateOnly? DateFor(GetUserBirthdaysResponse response, string userId) =>
        response.Birthdays.Single(b => b.UserId == userId).BirthDate;

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ReturnsTheStoredBirthDate()
    {
        var userId = await SeedUserAsync(new DateOnly(1990, 3, 4));

        Assert.That(DateFor(await Get(userId), userId), Is.EqualTo(new DateOnly(1990, 3, 4)));
    }

    [Test]
    public async Task Handle_AnswersEveryIdInOneRoundTrip()
    {
        var first = await SeedUserAsync(new DateOnly(1990, 3, 4));
        var second = await SeedUserAsync(new DateOnly(1985, 12, 31));

        var response = await Get(first, second);

        Assert.That(response.Birthdays.Select(b => b.UserId), Is.EquivalentTo(new[] { first, second }));
        Assert.Multiple(() =>
        {
            Assert.That(DateFor(response, first), Is.EqualTo(new DateOnly(1990, 3, 4)));
            Assert.That(DateFor(response, second), Is.EqualTo(new DateOnly(1985, 12, 31)));
        });
    }

    [Test]
    public async Task Handle_FriendsVisibility_StillAnswers()
    {
        // The per-viewer decision belongs to Social, which knows the reader's relation to the
        // subject. Identity only refuses the case where no viewer could ever be admitted.
        var userId = await SeedUserAsync(new DateOnly(1990, 3, 4), Visibility.Friends);

        Assert.That(DateFor(await Get(userId), userId), Is.EqualTo(new DateOnly(1990, 3, 4)));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_EmptyRequest_ReturnsNothingRatherThanEveryone()
    {
        await SeedUserAsync(new DateOnly(1990, 3, 4));

        Assert.That((await Get()).Birthdays, Is.Empty);
    }

    [Test]
    public async Task Handle_DuplicateAndBlankIds_AreNormalised()
    {
        var userId = await SeedUserAsync(new DateOnly(1990, 3, 4));

        var response = await Get(userId, userId, "", "   ");

        Assert.That(response.Birthdays, Has.Count.EqualTo(1));
        Assert.That(response.Birthdays.Single().UserId, Is.EqualTo(userId));
    }

    [Test]
    public async Task Handle_DoesNotTrackTheRowsItReads()
    {
        var userId = await SeedUserAsync(new DateOnly(1990, 3, 4));
        _ctx.ChangeTracker.Clear();

        await Get(userId);

        Assert.That(_ctx.ChangeTracker.Entries<ApplicationUser>(), Is.Empty);
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_VisibilityNobody_RefusesOutright()
    {
        // Nobody is the shipped default. There is no viewer for whom answering could be correct, so
        // the date never leaves this service - the viewer-independent floor described on the handler.
        var userId = await SeedUserAsync(new DateOnly(1990, 3, 4), Visibility.Nobody);

        Assert.That(DateFor(await Get(userId), userId), Is.Null);
    }

    [Test]
    public async Task Handle_UnknownUser_IsAnsweredWithNullRatherThanOmitted()
    {
        var response = await Get("user_doesnotexist");

        Assert.That(response.Birthdays, Has.Count.EqualTo(1));
        Assert.That(DateFor(response, "user_doesnotexist"), Is.Null,
            "an id with no answer must still get an entry, or the caller invents a fallback");
    }

    [Test]
    public async Task Handle_PurgedAccount_ReportsNoBirthday()
    {
        // Tombstone() clears both the flat column and the AgeVerification copy (T1-9). What must not
        // happen is the cleared DateOnly surfacing as 0001-01-01.
        var userId = await SeedUserAsync(new DateOnly(1990, 3, 4));

        var user = await _ctx.Users.FirstAsync(u => u.Id == userId);
        user.Tombstone();
        await _ctx.SaveChangesAsync();

        Assert.That(DateFor(await Get(userId), userId), Is.Null);
    }

    [Test]
    public async Task Handle_BotAccount_ReportsNoBirthday()
    {
        // A bot has no recorded date at all - default(DateOnly) - and no settings visibility that
        // would admit one anyway.
        var bot = ApplicationUser.CreateBot($"user_bot{Guid.NewGuid():N}"[..20], $"bot{Guid.NewGuid():N}"[..12]);
        bot.UserPrivacySettings!.BirthdayVisibility = Visibility.Everyone;
        _ctx.Users.Add(bot);
        await _ctx.SaveChangesAsync();

        Assert.That(DateFor(await Get(bot.Id), bot.Id), Is.Null);
    }

    [Test]
    public async Task Handle_AccountWithNoSettingsRow_RefusesRatherThanGuessing()
    {
        // A row is minted on create and backfilled by migration, so its absence is unexpected - and
        // the safe reading of "unexpected" is Nobody, not the shipped default.
        var userId = await SeedUserAsync(new DateOnly(1990, 3, 4));

        var settings = await _ctx.UserPrivacySettings.FirstAsync(p => p.UserId == userId);
        _ctx.UserPrivacySettings.Remove(settings);
        await _ctx.SaveChangesAsync();
        _ctx.ChangeTracker.Clear();

        Assert.That(DateFor(await Get(userId), userId), Is.Null);
    }
}
