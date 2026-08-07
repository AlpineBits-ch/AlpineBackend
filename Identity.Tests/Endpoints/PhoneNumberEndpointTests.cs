using Identity.Application.Dtos.Request;
using Identity.Application.Endpoints;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Endpoints;

/// <summary>The account's self-entered phone number.</summary>
[TestFixture]
public class PhoneNumberEndpointTests
{
    private const string UserId = "user-anna";
    private const string OtherUserId = "user-ben";

    private TestIdentityContext _context = null!;
    private SessionDeviceResolver _sessionDevices = null!;
    private FakeIdentityMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestIdentityContext(Guid.NewGuid().ToString());
        _sessionDevices = new SessionDeviceResolver(_context);
        _bus = new FakeIdentityMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    /// <summary>Built through the factory rather than <c>new ApplicationUser { ... }</c>: the
    /// aggregate owns a <c>UserPreferences</c> whose id is minted in <c>Create</c>, and constructing
    /// it directly leaves that navigation with a null primary key, which EF refuses to track.</summary>
    private async Task<ApplicationUser> SeedUser(string userId, string? phoneNumber = null)
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

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }

    private Task<IResult> Put(string userId, string? phoneNumber) =>
        PhoneNumberEndpoint.Put(new SetPhoneNumberDto { PhoneNumber = phoneNumber },
            TestPrincipal.ForUser(userId), _sessionDevices, _context);

    private Task<IResult> Delete(string userId) =>
        PhoneNumberEndpoint.Delete(TestPrincipal.ForUser(userId), _sessionDevices, _bus, _context);

    private List<UserPhoneNumberRemovedEvent> RemovalEvents() =>
        _bus.Published.OfType<UserPhoneNumberRemovedEvent>().ToList();

    private async Task<ApplicationUser> Reload(string userId) =>
        await _context.Users.AsNoTracking().FirstAsync(u => u.Id == userId);

    private async Task<List<IdentityAuditEvent>> AuditRows(string userId) =>
        await _context.IdentityAuditEvents.AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync();

    // ══════════════════════════════════════════════════════════════════════════ Normal
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Put_RecordsTheNumberOnTheCallersOwnAccount()
    {
        await SeedUser(UserId);

        var result = await Put(UserId, "+41791234567");

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.Not.InstanceOf<BadRequest<string>>());
            Assert.That((await Reload(UserId)).PhoneNumber, Is.EqualTo("+41791234567"));
        });
    }

    [Test]
    public async Task Put_NormalisesBeforeStoring()
    {
        // Stored one way regardless of how it was typed, because it is read back and copied into a
        // banking app by hand - a number the user has to tidy up at that moment is one they can get
        // wrong at that moment.
        await SeedUser(UserId);

        await Put(UserId, "+41 (79) 123-45-67");

        Assert.That((await Reload(UserId)).PhoneNumber, Is.EqualTo("+41791234567"));
    }

    [Test]
    public async Task Put_ReplacingANumberAuditsTheChangeWithBothSidesMasked()
    {
        await SeedUser(UserId, "+41790000000");

        await Put(UserId, "+41791234567");

        var rows = await AuditRows(UserId);
        var row = rows.Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.Action, Is.EqualTo(IdentityAuditActions.PhoneNumberChanged));
            Assert.That(row.Detail, Is.EqualTo("+41***00 -> +41***67"));
            Assert.That(row.Detail, Does.Not.Contain("791234567"),
                "the audit table is append-only and never tidied - a full number in it is a second "
                + "copy of the account's most re-identifying field, kept forever");
        });
    }

    [Test]
    public async Task Delete_ClearsTheNumberAndAuditsIt()
    {
        await SeedUser(UserId, "+41791234567");

        var result = await Delete(UserId);

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That((await Reload(UserId)).PhoneNumber, Is.Null);
            Assert.That((await AuditRows(UserId)).Single().Action,
                Is.EqualTo(IdentityAuditActions.PhoneNumberRemoved));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ The two decisions
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Put_NeverStampsPhoneVerifiedAt()
    {
        // The load-bearing assertion in this file.
        await SeedUser(UserId);

        await Put(UserId, "+41791234567");

        var user = await Reload(UserId);
        Assert.Multiple(() =>
        {
            Assert.That(user.PhoneVerifiedAt, Is.Null);
            Assert.That(user.PhoneNumberConfirmed, Is.False,
                "ASP.NET Identity's own flag stays false too - setting it would make UserManager's "
                + "SMS two-factor flows believe they have a confirmed channel");
        });
    }

    [Test]
    public async Task Put_TwoAccountsMayHoldTheSameNumber()
    {
        // Deliberate, and the decision most likely to be argued with.
        await SeedUser(UserId);
        await SeedUser(OtherUserId);

        var first = await Put(UserId, "+41791234567");
        var second = await Put(OtherUserId, "+41791234567");

        Assert.Multiple(async () =>
        {
            Assert.That(first, Is.Not.InstanceOf<BadRequest<string>>());
            Assert.That(second, Is.Not.InstanceOf<BadRequest<string>>(),
                "the second claim must not be refused - see the class remarks");
            Assert.That((await Reload(UserId)).PhoneNumber, Is.EqualTo("+41791234567"));
            Assert.That((await Reload(OtherUserId)).PhoneNumber, Is.EqualTo("+41791234567"));
        });
    }

    [Test]
    public async Task Put_WritesOnlyTheCallersOwnRecord()
    {
        // There is no route parameter for whose number is being written, so this is really an
        // assertion about the shape of the endpoint: the other account must be untouched by any
        // call to it.
        await SeedUser(UserId);
        await SeedUser(OtherUserId, "+41790000000");

        await Put(UserId, "+41791234567");

        Assert.That((await Reload(OtherUserId)).PhoneNumber, Is.EqualTo("+41790000000"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Edge
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Put_TheSameNumberAgainSucceedsAndAuditsNothing()
    {
        // A settings form that resubmits unchanged has not done anything worth a row on a security
        // timeline, and a timeline full of no-ops is one nobody reads.
        await SeedUser(UserId, "+41791234567");

        var result = await Put(UserId, "+41 79 123 45 67");

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.Not.InstanceOf<BadRequest<string>>());
            Assert.That(await AuditRows(UserId), Is.Empty);
        });
    }

    [Test]
    public async Task Delete_WithNoNumberIsNoContentAndAuditsNothing()
    {
        // Idempotent, and the same answer either way.
        await SeedUser(UserId);

        var result = await Delete(UserId);

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(await AuditRows(UserId), Is.Empty);
        });
    }

    [Test]
    public async Task Put_FirstNumberAuditsAsASetRatherThanAChangeFromNothing()
    {
        await SeedUser(UserId);

        await Put(UserId, "+41791234567");

        Assert.That((await AuditRows(UserId)).Single().Detail, Is.EqualTo("set to +41***67"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Negative
    // ══════════════════════════════════════════════════════════════════════════

    [TestCase(null)]
    [TestCase("")]
    [TestCase("0791234567")]
    [TestCase("+41 79 CALL ME")]
    [TestCase("+0791234567")]
    public async Task Put_AMalformedNumberIsRefusedAndNothingIsWritten(string? typed)
    {
        await SeedUser(UserId, "+41790000000");

        var result = await Put(UserId, typed);

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That((await Reload(UserId)).PhoneNumber, Is.EqualTo("+41790000000"),
                "a refused write must not have half-happened");
            Assert.That(await AuditRows(UserId), Is.Empty);
        });
    }

    [Test]
    public async Task Put_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedUser(UserId);

        var result = await PhoneNumberEndpoint.Put(new SetPhoneNumberDto { PhoneNumber = "+41791234567" },
            TestPrincipal.Anonymous(), _sessionDevices, _context);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Delete_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedUser(UserId, "+41791234567");

        var result = await PhoneNumberEndpoint.Delete(
            TestPrincipal.Anonymous(), _sessionDevices, _bus, _context);

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That((await Reload(UserId)).PhoneNumber, Is.EqualTo("+41791234567"));
            Assert.That(RemovalEvents(), Is.Empty,
                "an unauthenticated call names no account, so there is no consent it could revoke");
        });
    }

    [Test]
    public async Task Put_ForAnAccountThatDoesNotExist_ReturnsNotFound()
    {
        var result = await Put("user-nobody", "+41791234567");

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════ Revoking the guild
    // opt-ins ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Delete_PublishesTheRemovalSoGuildDropsEveryOptIn()
    {
        // Identity does not reach into Guild's rows - it has no guild model and should not grow one
        // - so the withdrawal travels as a fact about this account and Guild decides what it means
        // for its own state.
        await SeedUser(UserId, "+41791234567");

        await Delete(UserId);

        Assert.That(RemovalEvents().Single().UserId, Is.EqualTo(UserId));
    }

    [Test]
    public async Task Delete_TheEventCarriesNoPhoneNumber()
    {
        // The load-bearing assertion about the contract.
        await SeedUser(UserId, "+41791234567");

        await Delete(UserId);

        var payload = System.Text.Json.JsonSerializer.Serialize(RemovalEvents().Single());

        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Not.Contain("791234567"));
            Assert.That(payload, Does.Not.Contain("+41"));
        });
    }

    [Test]
    public async Task Delete_WithNoNumberStillPublishes()
    {
        // Deliberately not short-circuited.
        await SeedUser(UserId);

        var result = await Delete(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(RemovalEvents().Single().UserId, Is.EqualTo(UserId));
        });
    }

    [Test]
    public async Task Put_ReplacingTheNumberRevokesNothing()
    {
        // The decision most likely to be revisited, so it is pinned.
        await SeedUser(UserId, "+41790000000");

        await Put(UserId, "+41791234567");

        Assert.That(RemovalEvents(), Is.Empty);
    }

    [Test]
    public async Task Put_RecordingAFirstNumberRevokesNothingEither()
    {
        await SeedUser(UserId);

        await Put(UserId, "+41791234567");

        Assert.That(RemovalEvents(), Is.Empty);
    }

    [Test]
    public async Task Delete_ForAnAccountThatDoesNotExist_PublishesNothing()
    {
        // The 404 path.
        var result = await PhoneNumberEndpoint.Delete(
            TestPrincipal.ForUser("user-nobody"), _sessionDevices, _bus, _context);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound>());
            Assert.That(RemovalEvents(), Is.Empty);
        });
    }
}
