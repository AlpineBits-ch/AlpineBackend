using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;

namespace Identity.Tests.Consumers;

/// <summary>The phone-number directory Guild's payments page reads.</summary>
[TestFixture]
public class GetUserPhoneNumbersHandlerTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task<ApplicationUser> SeedUser(
        string userId,
        string? phoneNumber = "+41791234567",
        UserStatus status = UserStatus.Active,
        UserType userType = UserType.Default)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"phone-{Guid.NewGuid():N}@test.invalid",
            PhoneNumber = phoneNumber!,
            Username = $"phone-{userId}",
            BirthDate = new DateOnly(2000, 1, 1),
        });

        user.Id = userId;
        user.PhoneNumber = phoneNumber;
        user.Status = status;
        user.UserType = userType;

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }

    private Task<Identity.Contracts.Bus.Response.GetUserPhoneNumbersResponse> Handle(params string[] userIds) =>
        GetUserPhoneNumbersHandler.Handle(new GetUserPhoneNumbersRequest { UserIds = userIds }, _context);

    // ══════════════════════════════════════════════════════════════════════════ Normal
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReturnsTheNumberOfEachRequestedAccountThatHasOne()
    {
        await SeedUser("user-anna", "+41791111111");
        await SeedUser("user-ben", "+41792222222");

        var response = await Handle("user-anna", "user-ben");

        Assert.That(response.PhoneNumbers.Select(p => (p.UserId, p.PhoneNumber)),
            Is.EquivalentTo(new[]
            {
                ("user-anna", "+41791111111"),
                ("user-ben", "+41792222222"),
            }));
    }

    [Test]
    public async Task AnswersOnlyForTheIdsItWasAsked()
    {
        // The request is the whole of the scope.
        await SeedUser("user-anna", "+41791111111");
        await SeedUser("user-ben", "+41792222222");

        var response = await Handle("user-anna");

        Assert.That(response.PhoneNumbers.Select(p => p.UserId), Is.EquivalentTo(new[] { "user-anna" }));
    }

    [Test]
    public async Task CarriesWhenTheNumberWasLastWritten()
    {
        // With nothing verifying the number, recency is the only signal a client has.
        var user = await SeedUser("user-anna", "+41791111111");

        var response = await Handle("user-anna");

        Assert.That(response.PhoneNumbers.Single().UpdatedAt, Is.EqualTo(user.UpdatedAt));
    }

    // ══════════════════════════════════════════════════════════════════════════ Edge
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AnAccountWithNoNumberIsAbsentRatherThanReported()
    {
        // Absence has to be the only outcome, with nothing beside it saying why.
        await SeedUser("user-anna", "+41791111111");
        await SeedUser("user-ben", phoneNumber: null);

        var response = await Handle("user-anna", "user-ben");

        Assert.Multiple(() =>
        {
            Assert.That(response.PhoneNumbers.Select(p => p.UserId), Is.EquivalentTo(new[] { "user-anna" }));
            Assert.That(response.OmittedUserIds, Is.Empty,
                "OmittedUserIds is for batch truncation only - using it here would be the reason "
                + "channel this contract must not have");
        });
    }

    [Test]
    public async Task AnUnknownAccountIsAbsentAndUnreported()
    {
        // Same rule as above, for the same reason: reporting it would make this a probe for which
        // account ids are real.
        await SeedUser("user-anna", "+41791111111");

        var response = await Handle("user-anna", "user-nobody");

        Assert.Multiple(() =>
        {
            Assert.That(response.PhoneNumbers.Select(p => p.UserId), Is.EquivalentTo(new[] { "user-anna" }));
            Assert.That(response.OmittedUserIds, Is.Empty);
        });
    }

    [Test]
    public async Task AnEmptyRequestIsAnEmptyAnswerNotAScan()
    {
        await SeedUser("user-anna", "+41791111111");

        var response = await Handle();

        Assert.That(response.PhoneNumbers, Is.Empty);
    }

    [Test]
    public async Task DuplicateIdsAreCollapsed()
    {
        await SeedUser("user-anna", "+41791111111");

        var response = await Handle("user-anna", "user-anna", "user-anna");

        Assert.That(response.PhoneNumbers, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ABatchOverTheCapIsTruncatedAndSaidSo()
    {
        // Silently answering for the first 500 of 600 would hand the client a page where some
        // housemates appear to have shared nothing - which is indistinguishable from them having
        // opted out, and is the one wrong answer that looks like a right one.
        await SeedUser("user-anna", "+41791111111");

        var ids = Enumerable.Range(0, GetUserPhoneNumbersRequest.MaxUserIds + 3)
            .Select(i => $"user-{i:D4}")
            .Append("user-anna")
            .ToArray();

        var response = await Handle(ids);

        Assert.That(response.OmittedUserIds, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task ResultsAreOrderedByUserId()
    {
        // Ordered in memory rather than in the query, because the StringComparer overload of
        // OrderBy has no SQL translation and a provider that quietly evaluates it client-side would
        // make this read as working until it met Postgres.
        await SeedUser("user-cara", "+41793333333");
        await SeedUser("user-anna", "+41791111111");
        await SeedUser("user-ben", "+41792222222");

        var response = await Handle("user-cara", "user-anna", "user-ben");

        Assert.That(response.PhoneNumbers.Select(p => p.UserId),
            Is.EqualTo(new[] { "user-anna", "user-ben", "user-cara" }));
    }

    // ══════════════════════════════════════════════════════════════════════════ Negative
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ATombstonedAccountIsNotAnswered()
    {
        // Tombstone already nulls the number, so this filter should be redundant.
        await SeedUser("user-anna", "+41791111111");
        await SeedUser("user-gone", "+41799999999", status: UserStatus.Deleted);

        var response = await Handle("user-anna", "user-gone");

        Assert.That(response.PhoneNumbers.Select(p => p.UserId), Is.EquivalentTo(new[] { "user-anna" }));
    }

    [Test]
    public async Task ABotAccountIsNotAnswered()
    {
        // A bot has no phone to enter, so a number on a bot row means somebody put it there.
        await SeedUser("user-anna", "+41791111111");
        await SeedUser("bot-1", "+41798888888", userType: UserType.Bot);

        var response = await Handle("user-anna", "bot-1");

        Assert.That(response.PhoneNumbers.Select(p => p.UserId), Is.EquivalentTo(new[] { "user-anna" }));
    }

    [Test]
    public async Task ModeratorsAndAdministratorsAreOrdinaryPeopleAndAreAnswered()
    {
        // The filter is "not a bot", not "is Default".
        await SeedUser("user-mod", "+41794444444", userType: UserType.Moderator);
        await SeedUser("user-admin", "+41795555555", userType: UserType.Admin);

        var response = await Handle("user-mod", "user-admin");

        Assert.That(response.PhoneNumbers.Select(p => p.UserId),
            Is.EquivalentTo(new[] { "user-admin", "user-mod" }));
    }

    [Test]
    public async Task BlankIdsAreDroppedRatherThanQueriedFor()
    {
        await SeedUser("user-anna", "+41791111111");

        var response = await GetUserPhoneNumbersHandler.Handle(
            new GetUserPhoneNumbersRequest { UserIds = ["user-anna", "", "   "] }, _context);

        Assert.That(response.PhoneNumbers.Select(p => p.UserId), Is.EquivalentTo(new[] { "user-anna" }));
    }
}
