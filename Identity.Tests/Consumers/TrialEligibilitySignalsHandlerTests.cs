using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;

namespace Identity.Tests.Consumers;

/// <summary>The account facts Billing reads before it hands out a trial.</summary>
[TestFixture]
public class TrialEligibilitySignalsHandlerTests
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
        bool emailVerified = true,
        UserStatus status = UserStatus.Active,
        UserType userType = UserType.Default,
        DateTimeOffset? phoneVerifiedAt = null)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"trial-{Guid.NewGuid():N}@test.invalid",
            PhoneNumber = phoneNumber!,
            Username = $"trial-{userId}",
            BirthDate = new DateOnly(2000, 1, 1),
        });

        user.Id = userId;
        user.PhoneNumber = phoneNumber;
        user.EmailVerifiedAt = emailVerified ? DateTimeOffset.UtcNow.AddDays(-30) : null;
        user.PhoneVerifiedAt = phoneVerifiedAt;
        user.Status = status;
        user.UserType = userType;

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }

    private async Task SeedDevice(
        string userId, string clientDeviceId, DeviceStatus status = DeviceStatus.Active)
    {
        _context.UserDevices.Add(new UserDevice
        {
            Id = UserDevice.GenerateId(),
            ClientDeviceId = clientDeviceId,
            UserId = userId,
            DeviceName = clientDeviceId,
            DeviceType = DeviceType.Desktop,
            IdentityPublicKey = [1, 2, 3],
            Status = status,
        });

        await _context.SaveChangesAsync();
    }

    private Task<GetTrialEligibilitySignalsResponse> Handle(string userId) =>
        TrialEligibilitySignalsHandler.Handle(
            new GetTrialEligibilitySignalsRequest { UserId = userId }, _context);

    // ══════════════════════════════════════════════════════════════════════════ Normal
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReturnsTheSignalsOfAnOrdinaryAccount()
    {
        var user = await SeedUser("user-anna");
        await SeedDevice("user-anna", "device-laptop");
        await SeedDevice("user-anna", "device-phone");

        var response = await Handle("user-anna");

        Assert.Multiple(() =>
        {
            Assert.That(response.Found, Is.True);
            Assert.That(response.EmailVerified, Is.True);
            Assert.That(response.PhoneNumber, Is.EqualTo("+41791234567"));
            Assert.That(response.CreatedAt, Is.EqualTo(user.CreatedAt));
            Assert.That(response.DeviceIds, Is.EquivalentTo(new[] { "device-laptop", "device-phone" }));
            Assert.That(response.Status, Is.EqualTo(nameof(UserStatus.Active)));
            Assert.That(response.IsBot, Is.False);
        });
    }

    [Test]
    public async Task AnUnverifiedEmailIsReportedAsSuch()
    {
        await SeedUser("user-anna", emailVerified: false);

        var response = await Handle("user-anna");

        Assert.Multiple(() =>
        {
            Assert.That(response.Found, Is.True);
            Assert.That(response.EmailVerified, Is.False);
        });
    }

    /// <summary>The caller needs the status to refuse anything that is not active, and it travels as a
    /// name because <c>UserStatus</c> is Identity's enum and Billing has no business referencing
    /// it.</summary>
    [Test]
    public async Task TheStatusTravelsAsItsName()
    {
        await SeedUser("user-banned", status: UserStatus.Banned);

        var response = await Handle("user-banned");

        Assert.That(response.Status, Is.EqualTo(nameof(UserStatus.Banned)));
    }

    // ══════════════════════════════════════════════════════════════════════════ Edge
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The one that must not change.</summary>
    [Test]
    public async Task AVerificationStampOnThePhoneColumnChangesNothing()
    {
        await SeedUser("user-anna", phoneVerifiedAt: null);
        await SeedUser("user-ben", phoneVerifiedAt: DateTimeOffset.UtcNow);

        var anna = await Handle("user-anna");
        var ben = await Handle("user-ben");

        Assert.Multiple(() =>
        {
            Assert.That(ben.PhoneNumber, Is.EqualTo(anna.PhoneNumber),
                "the number is the number either way");

            Assert.That(
                typeof(GetTrialEligibilitySignalsResponse).GetProperties()
                    .Select(property => property.Name)
                    .Where(name => name.Contains("Verified", StringComparison.OrdinalIgnoreCase)),
                Is.EquivalentTo(new[] { nameof(GetTrialEligibilitySignalsResponse.EmailVerified) }),
                "the only verification this platform can claim is the email one");
        });
    }

    [Test]
    public async Task AnAccountWithNoNumberReportsNullRatherThanEmpty()
    {
        await SeedUser("user-anna", phoneNumber: null);

        var response = await Handle("user-anna");

        Assert.Multiple(() =>
        {
            Assert.That(response.Found, Is.True);
            Assert.That(response.PhoneNumber, Is.Null);
        });
    }

    [Test]
    public async Task AnAccountWithNoDevicesReportsAnEmptyList()
    {
        await SeedUser("user-anna");

        var response = await Handle("user-anna");

        Assert.That(response.DeviceIds, Is.Empty);
    }

    /// <summary>Removed devices are excluded.</summary>
    [Test]
    public async Task ARemovedDeviceIsNotReported()
    {
        await SeedUser("user-anna");
        await SeedDevice("user-anna", "device-live");
        await SeedDevice("user-anna", "device-gone", DeviceStatus.Removed);

        var response = await Handle("user-anna");

        Assert.That(response.DeviceIds, Is.EquivalentTo(new[] { "device-live" }));
    }

    [Test]
    public async Task AnotherAccountsDevicesAreNotReported()
    {
        await SeedUser("user-anna");
        await SeedUser("user-ben");
        await SeedDevice("user-anna", "device-anna");
        await SeedDevice("user-ben", "device-ben");

        var response = await Handle("user-anna");

        Assert.That(response.DeviceIds, Is.EquivalentTo(new[] { "device-anna" }));
    }

    [Test]
    public async Task ABotIsReportedAsOne()
    {
        await SeedUser("bot-1", userType: UserType.Bot);

        var response = await Handle("bot-1");

        Assert.That(response.IsBot, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════ Negative
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>An account nobody has is <c>Found: false</c>, which the caller treats as a refusal
    /// rather than as an absent set of signals. The difference matters: every other field on a
    /// not-found response is a default, and a caller reading them as facts would decide that an
    /// account that does not exist has an unverified email and no devices.</summary>
    [Test]
    public async Task AnUnknownAccountIsNotFound()
    {
        await SeedUser("user-anna");

        var response = await Handle("user-nobody");

        Assert.Multiple(() =>
        {
            Assert.That(response.Found, Is.False);
            Assert.That(response.PhoneNumber, Is.Null);
            Assert.That(response.DeviceIds, Is.Empty);
        });
    }

    [Test]
    public async Task ABlankRequestIsNotFoundRatherThanAScan()
    {
        await SeedUser("user-anna");

        var blank = await Handle("   ");
        var empty = await Handle(string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(blank.Found, Is.False);
            Assert.That(empty.Found, Is.False);
        });
    }

    [Test]
    public void ANullRequestThrowsRatherThanAnsweringForNobody()
    {
        Assert.That(
            async () => await TrialEligibilitySignalsHandler.Handle(null!, _context),
            Throws.InstanceOf<ArgumentNullException>());
    }
}
