using Domain;
using Identity.Application.Dtos.Request;
using Identity.Application.Endpoints;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Domain.ValueObjects;
using Identity.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Endpoints;

/// <summary>
/// The account's device-admission protection level.
///
/// <para>The attack this exists to close: if the level were a plain server-side boolean, a hostile
/// server could flip a strict account to permissive and then auto-admit a device of its own,
/// defeating the entire tier. So the authority is a signed assertion, the column is a cache, and a
/// downgrade costs the account password and leaves an audit row.</para>
///
/// <para>Note what is <b>not</b> tested here, deliberately: whether a signature is valid. The server
/// cannot check it and must not appear to - the clients verify it against the user's identity key,
/// and a server-side verdict is exactly the thing they are not supposed to trust.</para>
/// </summary>
[TestFixture]
public class ProtectionLevelEndpointTests
{
    private const string UserId = "user-1";
    private const string Password = "Correct-Horse-1!";

    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static readonly byte[] Assertion = Enumerable.Repeat((byte)0x5A, 64).ToArray();

    private async Task<ApplicationUser> SeedUser(
        ProtectionLevel level = ProtectionLevel.TrustedSignIn,
        int version = 0,
        bool withIdentityKey = true,
        bool withRecoveryCode = true)
    {
        // Built through the factory, not `new ApplicationUser { ... }`: the aggregate owns a
        // UserPreferences whose id is minted in Create, and constructing the user directly leaves
        // that navigation with a null primary key, which EF refuses to track.
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"protection-{Guid.NewGuid():N}@test.invalid",
            PhoneNumber = "+10000000000",
            Username = "protection-user",
            BirthDate = new DateOnly(2000, 1, 1),
        });

        user.Id = UserId;
        user.ProtectionLevel = level;
        user.ProtectionLevelVersion = version;
        user.AccountIdentityPublicKey = withIdentityKey ? [1, 2, 3] : null;
        user.AccountIdentityKeyVersion = withIdentityKey ? 1 : 0;

        // The password wrapping always exists once encryption is set up; the recovery-code wrapping
        // is the one the strict tier requires, because it is the only credential a password reset
        // leaves intact.
        user.EncryptedMasterKey = new EncryptedMasterKey
        {
            CipherText = [1], Salt = [2], Iv = [3], Version = 1,
        };
        user.RecoveryCodeWrappedMasterKey = withRecoveryCode
            ? new EncryptedMasterKey { CipherText = [4], Salt = [5], Iv = [6], Version = 1 }
            : null;

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private async Task SeedDevice(string deviceId, params string[] capabilities)
    {
        var device = UserDevice.Create(new CreateUserDeviceParams
        {
            UserId = UserId,
            ClientDeviceId = deviceId,
            DeviceName = deviceId,
            DeviceType = DeviceType.Desktop,
            IdentityPublicKey = [1],
        });
        device.Capabilities = capabilities.ToList();
        _context.UserDevices.Add(device);
        await _context.SaveChangesAsync();
    }

    /// <summary>UserManager is only ever asked to check a password here, and the InMemory provider
    /// cannot back a real one - so this stands in for exactly that one call.</summary>
    private static UserManager<ApplicationUser> ManagerAccepting(string password) =>
        new StubUserManager(password);

    private sealed class StubUserManager(string password)
        : UserManager<ApplicationUser>(new StubUserStore(), null!, new PasswordHasher<ApplicationUser>(),
            [], [], null!, null!, null!, null!)
    {
        public override Task<bool> CheckPasswordAsync(ApplicationUser user, string plain) =>
            Task.FromResult(plain == password);
    }

    private sealed class StubUserStore : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id);
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) => Task.CompletedTask;
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult<ApplicationUser?>(null);
    }

    private static PutProtectionLevelDto Put(ProtectionLevel level, int version, string? password = null) => new()
    {
        Level = level,
        Version = version,
        SignedAssertion = Assertion,
        Password = password,
        DeviceId = "device-a",
    };

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Put_WithoutASignedAssertion_IsRejected()
    {
        await SeedUser();
        await SeedDevice("device-a", MlsCapabilities.ProtectionLevelV1);

        var dto = Put(ProtectionLevel.VerifiedDevices, 1);
        dto.SignedAssertion = [];

        var (result, _) = await ProtectionLevelEndpoint.Put(
            dto, TestPrincipal.ForUser(UserId), ManagerAccepting(Password), _context);

        // An unsigned level is not a level. Accepting one makes the server the authority again by
        // the back door, asserting something no client can check.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Put_WithAStaleVersion_Is409WithTheCurrentState()
    {
        await SeedUser(version: 5);
        await SeedDevice("device-a", MlsCapabilities.ProtectionLevelV1);

        var (result, _) = await ProtectionLevelEndpoint.Put(
            Put(ProtectionLevel.VerifiedDevices, version: 5), TestPrincipal.ForUser(UserId),
            ManagerAccepting(Password), _context);

        // Two devices changing the level at once must not end with the account on the loser's
        // setting and the winner's assertion.
        Assert.That(result, Is.InstanceOf<Conflict<ProtectionLevelDto>>());
        Assert.That(((Conflict<ProtectionLevelDto>)result).Value!.Version, Is.EqualTo(5));
    }

    [Test]
    public async Task Upgrade_NeedsNoPassword_AndIsBroadcast()
    {
        await SeedUser();
        await SeedDevice("device-a", MlsCapabilities.ProtectionLevelV1);

        var (result, evt) = await ProtectionLevelEndpoint.Put(
            Put(ProtectionLevel.VerifiedDevices, version: 1), TestPrincipal.ForUser(UserId),
            ManagerAccepting(Password), _context);

        // Tightening your own account should never be gated.
        Assert.That(result, Is.InstanceOf<Ok<ProtectionLevelDto>>());
        Assert.Multiple(() =>
        {
            Assert.That(evt!.Level, Is.EqualTo(ProtectionLevel.VerifiedDevices));
            Assert.That(evt.IsDowngrade, Is.False);
        });
    }

    [Test]
    public async Task Upgrade_IsBlockedByADeviceThatCannotEnforceIt()
    {
        await SeedUser();
        await SeedDevice("device-a", MlsCapabilities.ProtectionLevelV1);
        await SeedDevice("device-old");

        var (result, _) = await ProtectionLevelEndpoint.Put(
            Put(ProtectionLevel.VerifiedDevices, version: 1), TestPrincipal.ForUser(UserId),
            ManagerAccepting(Password), _context);

        // A strict setting that one of your devices silently ignores is worse than no setting,
        // because the user believes they are protected. The blocking device is named so the UI can
        // say which one rather than failing opaquely.
        Assert.That(result, Is.InstanceOf<BadRequest<ProtectionLevelUpgradeBlockedDto>>());
        var blocked = (BadRequest<ProtectionLevelUpgradeBlockedDto>)result;
        Assert.That(blocked.Value!.BlockingDeviceNames, Is.EquivalentTo(new[] { "device-old" }));
    }

    [Test]
    public async Task Upgrade_IsBlockedWithoutARecoveryCodeEnvelope()
    {
        await SeedUser(withRecoveryCode: false);
        await SeedDevice("device-a", MlsCapabilities.ProtectionLevelV1);

        var (result, _) = await ProtectionLevelEndpoint.Put(
            Put(ProtectionLevel.VerifiedDevices, version: 1), TestPrincipal.ForUser(UserId),
            ManagerAccepting(Password), _context);

        // The tier promises a password reset cannot restore encrypted history. That is only true
        // while the recovery credential is not the password - turning it on beforehand would sell a
        // guarantee the storage layer does not implement.
        Assert.That(result, Is.InstanceOf<BadRequest<ProtectionLevelUpgradeBlockedDto>>());
    }

    [Test]
    public async Task Downgrade_WithoutThePassword_IsRejected()
    {
        await SeedUser(ProtectionLevel.VerifiedDevices);
        await SeedDevice("device-a", MlsCapabilities.ProtectionLevelV1);

        var (result, evt) = await ProtectionLevelEndpoint.Put(
            Put(ProtectionLevel.TrustedSignIn, version: 1), TestPrincipal.ForUser(UserId),
            ManagerAccepting(Password), _context);

        // Loosening the account is the move an attacker holding a session wants to make.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(evt, Is.Null);
        Assert.That((await _context.Users.SingleAsync()).ProtectionLevel,
            Is.EqualTo(ProtectionLevel.VerifiedDevices));
    }

    [Test]
    public async Task Downgrade_WithThePassword_IsBroadcastAndAudited()
    {
        await SeedUser(ProtectionLevel.VerifiedDevices);
        await SeedDevice("device-a", MlsCapabilities.ProtectionLevelV1);

        var (result, evt) = await ProtectionLevelEndpoint.Put(
            Put(ProtectionLevel.TrustedSignIn, version: 1, password: Password),
            TestPrincipal.ForUser(UserId), ManagerAccepting(Password), _context);

        Assert.That(result, Is.InstanceOf<Ok<ProtectionLevelDto>>());
        Assert.Multiple(() =>
        {
            // A client that sees a downgrade it did not participate in must warn loudly - it can
            // only do that if it is told, and the audit row is the record that survives the session.
            Assert.That(evt!.IsDowngrade, Is.True);
            Assert.That(evt.PreviousLevel, Is.EqualTo(ProtectionLevel.VerifiedDevices));
        });
        Assert.That(
            _context.IdentityAuditEvents.Any(a => a.Action == IdentityAuditActions.ProtectionLevelChanged),
            Is.True);
    }
}
