using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Endpoints;
using Identity.Application.Services;
using Identity.Contracts.Bus.Events;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Endpoints;

/// <summary>Device registration and the key-package stock behind it.</summary>
[TestFixture]
public class MlsDeviceEndpointTests
{
    private const string UserId = "user-1";
    private const string ClientDeviceId = "device-a";

    private const string Password = "Correct-Horse-1!";

    private TestIdentityContext _context = null!;
    private MlsDeviceEndpoint _endpoint = null!;
    private FakePasswordVerifier _passwords = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestIdentityContext(Guid.NewGuid().ToString());
        _endpoint = new MlsDeviceEndpoint();
        _passwords = new FakePasswordVerifier(Password);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private SessionDeviceResolver Sessions => TestSessions.Resolver(_context);

    /// <summary>A caller signed in <i>on</i> the named device, which is how the destructive device
    /// operations are reached without the account password.</summary>
    private Task<ClaimsPrincipal> SignedInOn(UserDevice device) =>
        TestSessions.SignedInOnAsync(_context, device.UserId, device.Id);

    private async Task<UserDevice> SeedDevice(
        string userId = UserId, string clientDeviceId = ClientDeviceId, byte[]? identityKey = null,
        ProtectionLevel level = ProtectionLevel.TrustedSignIn)
    {
        if (!await _context.Users.AnyAsync(u => u.Id == userId))
        {
            // Built through the factory, not `new ApplicationUser { ... }`: the aggregate owns a
            // UserPreferences whose id is minted in Create, and constructing the user directly
            // leaves that navigation with a null primary key, which EF refuses to track.
            var user = ApplicationUser.Create(new CreateUserParams
            {
                Email = $"{userId}-{Guid.NewGuid():N}@test.invalid",
                PhoneNumber = "+10000000000",
                Username = userId,
                BirthDate = new DateOnly(2000, 1, 1),
            });
            user.Id = userId;
            user.ProtectionLevel = level;
            _context.Users.Add(user);
        }

        var device = UserDevice.Create(new CreateUserDeviceParams
        {
            UserId = userId,
            ClientDeviceId = clientDeviceId,
            DeviceName = "Test device",
            DeviceType = DeviceType.Desktop,
            IdentityPublicKey = identityKey ?? [1, 2, 3],
        });
        _context.UserDevices.Add(device);
        await _context.SaveChangesAsync();
        return device;
    }

    private async Task SeedKeyPackages(string deviceRowId, int count, bool includeLastResort = true)
    {
        for (var i = 0; i < count; i++)
        {
            _context.UserKeyPackages.Add(UserKeyPackage.Create(new CreateUserKeyPackageParams
            {
                UserId = UserId,
                DeviceId = deviceRowId,
                KeyPackage = [0x00, 0x01, 0x00, 0x01, (byte)i],
                ExpiresAt = DateTime.UtcNow.AddDays(30),
            }));
        }

        if (includeLastResort)
        {
            _context.UserKeyPackages.Add(UserKeyPackage.Create(new CreateUserKeyPackageParams
            {
                UserId = UserId,
                DeviceId = deviceRowId,
                KeyPackage = [0x00, 0x01, 0x00, 0x01, 0xFF],
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                IsLastResort = true,
            }));
        }

        await _context.SaveChangesAsync();
    }

    private static CreateMLSDeviceDto RegisterDto(byte[]? identityKey = null, List<string>? capabilities = null) => new()
    {
        ClientDeviceId = ClientDeviceId,
        DeviceName = "Test device",
        DeviceType = DeviceType.Desktop,
        IdentityPublicKey = identityKey ?? [1, 2, 3],
        Capabilities = capabilities,
    };

    // ══════════════════════════════════════════════════════════════════════════
    // DELETE .../key-packages - the reset that makes a local wipe recoverable
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ResetKeyPackages_RemovesEverythingIncludingTheLastResortAndConsumedRows()
    {
        var device = await SeedDevice();
        await SeedKeyPackages(device.Id, count: 3);
        var consumed = await _context.UserKeyPackages.FirstAsync();
        consumed.ConsumedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var result = await MlsDeviceEndpoint.ResetKeyPackages(
            ClientDeviceId, null, await SignedInOn(device), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        // Consumed and last-resort rows are just as dead once the private halves are gone.
        Assert.That(((Ok<ResetKeyPackagesResultDto>)result).Value!.DeletedCount, Is.EqualTo(4));
        Assert.That(await _context.UserKeyPackages.AnyAsync(), Is.False);
    }

    [Test]
    public async Task ResetKeyPackages_IsIdempotent()
    {
        var device = await SeedDevice();
        await SeedKeyPackages(device.Id, count: 2);
        var caller = await SignedInOn(device);

        await MlsDeviceEndpoint.ResetKeyPackages(ClientDeviceId, null, caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();
        var second = await MlsDeviceEndpoint.ResetKeyPackages(
            ClientDeviceId, null, caller, _context, Sessions, _passwords);

        Assert.That(((Ok<ResetKeyPackagesResultDto>)second).Value!.DeletedCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ResetKeyPackages_ForAnUnknownDevice_IsNotFound()
    {
        await SeedDevice();

        var result = await MlsDeviceEndpoint.ResetKeyPackages(
            "device-nobody-has", null, TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ResetKeyPackages_ForAnotherUsersDevice_IsForbidden()
    {
        var other = await SeedDevice("user-2", "device-theirs");
        await SeedDevice();
        await SeedKeyPackages(other.Id, count: 2);

        var result = await MlsDeviceEndpoint.ResetKeyPackages(
            "device-theirs", null, TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        // ClientDeviceId is only unique per user, so "signed in as the wrong account" is a real and
        // distinguishable case - and it is the mistake that actually happens.
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(await _context.UserKeyPackages.CountAsync(), Is.EqualTo(3));
    }

    /// <summary>
    /// The attack: a stolen session, aimed at one of the account's other devices.
    /// </summary>
    [Test]
    public async Task ResetKeyPackages_ForAnotherOfMyDevices_NeedsThePassword()
    {
        var laptop = await SeedDevice(clientDeviceId: "device-laptop");
        var phone = await SeedDevice(clientDeviceId: "device-phone");
        await SeedKeyPackages(laptop.Id, count: 4);

        var onThePhone = await SignedInOn(phone);

        var refused = await MlsDeviceEndpoint.ResetKeyPackages(
            "device-laptop", null, onThePhone, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(refused, Is.InstanceOf<BadRequest<string>>());
        Assert.That(await _context.UserKeyPackages.CountAsync(), Is.EqualTo(5),
            "the laptop's stock must survive a request that could not prove the account");

        var allowed = await MlsDeviceEndpoint.ResetKeyPackages(
            "device-laptop", Password, onThePhone, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(allowed, Is.InstanceOf<Ok<ResetKeyPackagesResultDto>>());
        Assert.That(await _context.UserKeyPackages.AnyAsync(), Is.False);
        Assert.That(await _context.IdentityAuditEvents
            .AnyAsync(a => a.Action == IdentityAuditActions.KeyPackagesReset), Is.True,
            "the two destructive device operations must stop leaving no trace");
    }

    // ══════════════════════════════════════════════════════════════════════════ Registration and
    // identity rotation ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Register_WithAnUnchangedIdentityKey_DoesNotPurgeKeyPackages()
    {
        // An old client re-registers on every launch with the same key.
        var device = await SeedDevice(identityKey: [1, 2, 3]);
        await SeedKeyPackages(device.Id, count: 5);

        var (result, evt) = await _endpoint.CreateDevice(
            RegisterDto([1, 2, 3]), new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(((Ok<DeviceRegistrationDto>)result).Value!.IdentityRotated, Is.False);
            Assert.That(evt, Is.Null, "Nothing changed, so nothing is announced");
        });
        Assert.That(await _context.UserKeyPackages.CountAsync(), Is.EqualTo(6));
    }

    [Test]
    public async Task Register_WithARotatedIdentityKey_PurgesTheStockAndSaysSo()
    {
        var device = await SeedDevice(identityKey: [1, 2, 3]);
        await SeedKeyPackages(device.Id, count: 5);

        // From the device itself, which is the case a keychain miss actually produces.
        var (result, evt) = await _endpoint.CreateDevice(
            RegisterDto([9, 9, 9]), new FakeIdentityMessageBus(), await SignedInOn(device), _context,
            Sessions, _passwords);
        await _context.SaveChangesAsync();

        // Every package on file was minted under the identity that just went away.
        Assert.Multiple(() =>
        {
            Assert.That(((Ok<DeviceRegistrationDto>)result).Value!.IdentityRotated, Is.True);
            Assert.That(evt!.IdentityRotated, Is.True);
        });
        Assert.That(await _context.UserKeyPackages.AnyAsync(), Is.False);
        Assert.That((await _context.UserDevices.SingleAsync()).IdentityPublicKey, Is.EqualTo(new byte[] { 9, 9, 9 }));
    }

    [Test]
    public async Task Register_RecordsCapabilities()
    {
        var (_, _) = await _endpoint.CreateDevice(
            RegisterDto(capabilities: [MlsCapabilities.ProtectionLevelV1, MlsCapabilities.BackupV1]),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That((await _context.UserDevices.SingleAsync()).Capabilities,
            Is.EquivalentTo(new[] { MlsCapabilities.BackupV1, MlsCapabilities.ProtectionLevelV1 }));
    }

    [Test]
    public async Task Register_WithNoCapabilities_LeavesThePreviouslyRecordedSetAlone()
    {
        await SeedDevice();
        await _endpoint.CreateDevice(
            RegisterDto(capabilities: [MlsCapabilities.DeviceCertificateV1]),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        await _endpoint.CreateDevice(
            RegisterDto(capabilities: null), new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        // Absent means "told us nothing", not "supports nothing" - a partial re-registration must
        // not erase a device's declared support and quietly hold the account out of the strict tier.
        Assert.That((await _context.UserDevices.SingleAsync()).Capabilities,
            Is.EquivalentTo(new[] { MlsCapabilities.DeviceCertificateV1 }));
    }

    /// <summary>The attack: <c>capabilities: []</c> on a session token.</summary>
    [Test]
    public async Task Register_WithAnEmptyCapabilityList_CannotErasePreviouslyDeclaredSupport()
    {
        await SeedDevice();
        await _endpoint.CreateDevice(
            RegisterDto(capabilities: [MlsCapabilities.ProtectionLevelV1]),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        await _endpoint.CreateDevice(
            RegisterDto(capabilities: []),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That((await _context.UserDevices.SingleAsync()).Capabilities,
            Does.Contain(MlsCapabilities.ProtectionLevelV1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Device removal - the cascade takes the backup with it
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The attack: <c>DELETE /devices/client/{id}</c> aimed at another of the account's devices.
    /// </summary>
    [Test]
    public async Task RemoveDevice_AimedAtAnotherOfMyDevices_NeedsThePassword()
    {
        var laptop = await SeedDevice(clientDeviceId: "device-laptop");
        var phone = await SeedDevice(clientDeviceId: "device-phone");
        var onThePhone = await SignedInOn(phone);

        var (refused, noEvent) = await MlsDeviceEndpoint.RemoveDevice(
            "device-laptop", null, onThePhone, _context, Sessions, _passwords);

        Assert.That(refused, Is.InstanceOf<BadRequest<string>>());
        Assert.That(noEvent, Is.Null);
        Assert.That(await _context.UserDevices.AnyAsync(d => d.Id == laptop.Id), Is.True);

        var (allowed, removed) = await MlsDeviceEndpoint.RemoveDevice(
            "device-laptop", Password, onThePhone, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(allowed, Is.InstanceOf<NoContent>());
        Assert.That(removed!.ClientDeviceId, Is.EqualTo("device-laptop"));
    }

    [Test]
    public async Task RemoveDevice_FromTheDeviceItself_RevokesItsCertificate()
    {
        var device = await SeedDevice();
        device.Certificate = Enumerable.Repeat((byte)0xCD, 96).ToArray();
        device.CertificateIssuedAt = DateTimeOffset.UtcNow;
        device.CertificateExpiresAt = DateTimeOffset.UtcNow.AddDays(180);
        device.CertificateIdentityKeyVersion = 3;
        await _context.SaveChangesAsync();

        var (result, _) = await MlsDeviceEndpoint.RemoveDevice(
            ClientDeviceId, null, await SignedInOn(device), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());

        // The certificate is a public, self-contained, 180-day statement peers have already
        // downloaded. Dropping the server's copy changes nothing for them; the list does.
        var revoked = await _context.RevokedDeviceCertificates.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(revoked.ClientDeviceId, Is.EqualTo(ClientDeviceId));
            Assert.That(revoked.IdentityKeyVersion, Is.EqualTo(3));
            Assert.That(revoked.Reason, Is.EqualTo(CertificateRevocationReasons.DeviceRemoved));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Device certificates - structure and expiry only
    // ══════════════════════════════════════════════════════════════════════════

    private static CreateMLSDeviceDto WithCertificate(byte[] cert, DateTimeOffset issued, DateTimeOffset expires)
    {
        var dto = RegisterDto();
        dto.DeviceCertificate = cert;
        dto.CertificateIssuedAt = issued;
        dto.CertificateExpiresAt = expires;
        dto.CertificateIdentityKeyVersion = 1;
        return dto;
    }

    private static byte[] PlausibleCertificate => Enumerable.Repeat((byte)0xAB, 96).ToArray();

    [Test]
    public async Task Register_WithAWellFormedCertificate_StoresIt()
    {
        var now = DateTimeOffset.UtcNow;

        await _endpoint.CreateDevice(
            WithCertificate(PlausibleCertificate, now, now.AddDays(180)),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        var device = await _context.UserDevices.SingleAsync();
        Assert.That(device.HasValidCertificateAt(now), Is.True);
    }

    [Test]
    public async Task Register_WithAnAlreadyExpiredCertificate_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;

        var (result, _) = await _endpoint.CreateDevice(
            WithCertificate(PlausibleCertificate, now.AddDays(-200), now.AddDays(-20)),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);

        // Storing one guarantees a peer-side rejection later, and under the external-commit path a
        // removal proposal against a device whose only mistake was forgetting to reissue.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Register_WithACertificateClaimingAnAbsurdLifetime_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;

        var (result, _) = await _endpoint.CreateDevice(
            WithCertificate(PlausibleCertificate, now, now.AddDays(3650)),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);

        // Expiry is what stops a compromised device that nobody will reissue for from staying
        // verifiable. A ten-year certificate opts out of that.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Register_WithATruncatedCertificate_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;

        var (result, _) = await _endpoint.CreateDevice(
            WithCertificate([1, 2, 3], now, now.AddDays(30)),
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context, Sessions, _passwords);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // VerifiedDevices refuses the reusable last-resort package
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AddKeyPackages_LastResort_IsRefusedForAVerifiedDevicesAccount()
    {
        await SeedDevice(level: ProtectionLevel.VerifiedDevices);

        var result = await _endpoint.AddKeyPackagesForDeviceAsync(ClientDeviceId,
            new AddMLSDeviceKeyPackagesDto
            {
                KeyPackages =
                [
                    new PackageDto { KeyPackage = [0x00, 0x01, 0x00, 0x01, 1], IsLastResort = true },
                ],
            }, TestPrincipal.ForUser(UserId), _context);

        // Joining through a reusable package costs the leaf its forward secrecy from that point
        // back, which is most of what the strict tier is paying its usability cost for.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task AddKeyPackages_LastResort_IsAcceptedForATrustedSignInAccount()
    {
        await SeedDevice(level: ProtectionLevel.TrustedSignIn);

        var result = await _endpoint.AddKeyPackagesForDeviceAsync(ClientDeviceId,
            new AddMLSDeviceKeyPackagesDto
            {
                KeyPackages =
                [
                    new PackageDto { KeyPackage = [0x00, 0x01, 0x00, 0x01, 1], IsLastResort = true },
                ],
            }, TestPrincipal.ForUser(UserId), _context);

        Assert.That(result, Is.InstanceOf<Ok<AddKeyPackagesResultDto>>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // POST .../bind-session - the way out of an unbound session
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The deploy-day case.</summary>
    [Test]
    public async Task BindSession_WithThePassword_BindsAnUnboundSessionToAnExistingDevice()
    {
        var device = await SeedDevice();
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        var result = await MlsDeviceEndpoint.BindSessionToDevice(
            ClientDeviceId, new BindSessionDto { Password = Password }, caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(((Ok<BindSessionResultDto>)result).Value!.Bound, Is.True);
        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, device.Id), Is.True);
    }

    /// <summary>The reason the route can exist at all.</summary>
    [Test]
    public async Task BindSession_WithoutThePassword_ChangesNothing()
    {
        var device = await SeedDevice();
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        var result = await MlsDeviceEndpoint.BindSessionToDevice(
            ClientDeviceId, new BindSessionDto { Password = null }, caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, device.Id), Is.False);
        Assert.That(_passwords.Calls, Is.EqualTo(1), "The gate must actually reach the password check.");
    }

    [Test]
    public async Task BindSession_WithTheWrongPassword_ChangesNothing()
    {
        var device = await SeedDevice();
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        await MlsDeviceEndpoint.BindSessionToDevice(
            ClientDeviceId, new BindSessionDto { Password = "nope" }, caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, device.Id), Is.False);
    }

    /// <summary>A session does not change which handset it belongs to.</summary>
    [Test]
    public async Task BindSession_FromASessionAlreadyBoundElsewhere_IsAConflict()
    {
        var first = await SeedDevice();
        var second = await SeedDevice(clientDeviceId: "device-b");
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, first.Id);

        var result = await MlsDeviceEndpoint.BindSessionToDevice(
            "device-b", new BindSessionDto { Password = Password }, caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, second.Id), Is.False);
        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, first.Id), Is.True);
    }

    /// <summary>Idempotent, so a client retrying after a lost response is not told it conflicted with
    /// itself.</summary>
    [Test]
    public async Task BindSession_ToTheDeviceItIsAlreadyBoundTo_IsOkAndReportsNoChange()
    {
        var device = await SeedDevice();
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, device.Id);

        var result = await MlsDeviceEndpoint.BindSessionToDevice(
            ClientDeviceId, new BindSessionDto { Password = Password }, caller, _context, Sessions, _passwords);

        Assert.That(((Ok<BindSessionResultDto>)result).Value!.Bound, Is.False);
    }

    /// <summary>Binding is what decides which device's backup a session may read, so it leaves a
    /// durable trace - if a session is ever found reading the wrong blob, this row is where it
    /// started.</summary>
    [Test]
    public async Task BindSession_IsAudited()
    {
        await SeedDevice();
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        await MlsDeviceEndpoint.BindSessionToDevice(
            ClientDeviceId, new BindSessionDto { Password = Password }, caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        var audit = await _context.IdentityAuditEvents
            .SingleAsync(e => e.Action == IdentityAuditActions.SessionDeviceBound);
        Assert.That(audit.ClientDeviceId, Is.EqualTo(ClientDeviceId));
    }

    [Test]
    public async Task BindSession_ToAnotherAccountsDevice_IsForbidden()
    {
        await SeedDevice();
        await SeedDevice("user-2", "device-theirs");
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        var result = await MlsDeviceEndpoint.BindSessionToDevice(
            "device-theirs", new BindSessionDto { Password = Password }, caller, _context, Sessions, _passwords);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(_passwords.Calls, Is.Zero,
            "The device is resolved before the credential, so a wrong account cannot be used to probe "
            + "whether a password is right.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Registration from the installed base - claiming an unclaimed row
    // ══════════════════════════════════════════════════════════════════════════

    private async Task SeedBackup(UserDevice device)
    {
        _context.UserDeviceBackups.Add(UserDeviceBackup.Create(new CreateUserDeviceBackupParams
        {
            UserId = device.UserId,
            DeviceId = device.Id,
            Backup = Enumerable.Repeat((byte)0x7F, 64).ToArray(),
            Version = 1,
            RecoveryKeyVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        }));
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// The upgrade case, and the regression that took the whole mobile install base off the air.
    /// </summary>
    [Test]
    public async Task Register_FromASessionOlderThanTheDeviceRow_RotatesWithoutAPasswordAndClaimsTheRow()
    {
        var device = await SeedDevice(identityKey: [1, 2, 3]);
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        var (result, evt) = await _endpoint.CreateDevice(
            RegisterDto([9, 9, 9]), new FakeIdentityMessageBus(), caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<DeviceRegistrationDto>>());
            Assert.That(((Ok<DeviceRegistrationDto>)result).Value!.IdentityRotated, Is.True);
            Assert.That(evt!.IdentityRotated, Is.True);
            Assert.That(_passwords.Calls, Is.Zero, "The upgrade path must not demand a credential.");
        });

        Assert.That((await _context.UserDevices.SingleAsync()).IdentityPublicKey,
            Is.EqualTo(new byte[] { 9, 9, 9 }));

        // And the session is now that device, which is what makes the per-device backup and transfer
        // routes work again on the very same launch rather than one later.
        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, device.Id), Is.True);

        Assert.That(await _context.IdentityAuditEvents
            .AnyAsync(e => e.Action == IdentityAuditActions.SessionDeviceBound), Is.True,
            "Adoption is the concession this fix makes, so it leaves a durable trace.");
    }

    /// <summary>An ordinary re-registration claims the row too.</summary>
    [Test]
    public async Task Register_WithAnUnchangedKey_AlsoClaimsAnUnclaimedRow()
    {
        var device = await SeedDevice(identityKey: [1, 2, 3]);
        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        await _endpoint.CreateDevice(
            RegisterDto([1, 2, 3]), new FakeIdentityMessageBus(), caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, device.Id), Is.True);
    }

    /// <summary>§L.7, intact.</summary>
    [Test]
    public async Task Register_RotatingADeviceSomeSessionHasAlreadyClaimed_StillNeedsThePassword()
    {
        var laptop = await SeedDevice(identityKey: [1, 2, 3]);

        // The laptop's own session, which is what makes the row established.
        await TestSessions.SignedInOnAsync(_context, UserId, laptop.Id);

        var stolen = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        var (refused, noEvent) = await _endpoint.CreateDevice(
            RegisterDto([9, 9, 9]), new FakeIdentityMessageBus(), stolen, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.InstanceOf<BadRequest<string>>());
            Assert.That(noEvent, Is.Null);
        });
        Assert.That((await _context.UserDevices.SingleAsync()).IdentityPublicKey,
            Is.EqualTo(new byte[] { 1, 2, 3 }), "the established device's published key must survive");
        Assert.That(await Sessions.IsCallingDeviceAsync(stolen, UserId, laptop.Id), Is.False,
            "and the session must not have become that device on the way past");

        // The password is still the way through, exactly as it was.
        var dto = RegisterDto([9, 9, 9]);
        dto.Password = Password;
        var (allowed, _) = await _endpoint.CreateDevice(
            dto, new FakeIdentityMessageBus(), stolen, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(allowed, Is.InstanceOf<Ok<DeviceRegistrationDto>>());
    }

    /// <summary>A row that has never been signed in on but that holds an encrypted backup is not
    /// unclaimed in any sense that matters: adopting it hands over the one thing being that device
    /// grants. The password stays.</summary>
    [Test]
    public async Task Register_RotatingADeviceThatHoldsABackup_StillNeedsThePassword()
    {
        var laptop = await SeedDevice(identityKey: [1, 2, 3]);
        await SeedBackup(laptop);

        var caller = await TestSessions.SignedInOnAsync(_context, UserId, deviceRowId: null);

        var (refused, _) = await _endpoint.CreateDevice(
            RegisterDto([9, 9, 9]), new FakeIdentityMessageBus(), caller, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(refused, Is.InstanceOf<BadRequest<string>>());
        Assert.That(await Sessions.IsCallingDeviceAsync(caller, UserId, laptop.Id), Is.False);
    }

    /// <summary>A session that is already some device does not become another one, however unclaimed
    /// the target looks. Otherwise one compromised handset would collect the account.</summary>
    [Test]
    public async Task Register_FromASessionBoundToAnotherDevice_CannotRotateTheOtherDevice()
    {
        var phone = await SeedDevice(clientDeviceId: "device-phone");
        var laptop = await SeedDevice(clientDeviceId: ClientDeviceId, identityKey: [1, 2, 3]);
        var onThePhone = await SignedInOn(phone);

        var (refused, _) = await _endpoint.CreateDevice(
            RegisterDto([9, 9, 9]), new FakeIdentityMessageBus(), onThePhone, _context, Sessions, _passwords);
        await _context.SaveChangesAsync();

        Assert.That(refused, Is.InstanceOf<BadRequest<string>>());
        Assert.That(await Sessions.IsCallingDeviceAsync(onThePhone, UserId, laptop.Id), Is.False);
        Assert.That(await Sessions.IsCallingDeviceAsync(onThePhone, UserId, phone.Id), Is.True);
    }

    /// <summary>The wire form of <c>identityPublicKey</c>, pinned.</summary>
    [Test]
    public void IdentityPublicKey_OnTheWire_IsStandardBase64OfTheRawBytes()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var json = $$"""
                     {"clientDeviceId":"device-a","deviceName":"n","deviceType":"Mobile",
                      "identityPublicKey":"{{Convert.ToBase64String(raw)}}"}
                     """;

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        var dto = JsonSerializer.Deserialize<CreateMLSDeviceDto>(json, options)!;

        Assert.Multiple(() =>
        {
            Assert.That(dto.IdentityPublicKey, Is.EqualTo(raw));
            Assert.That(dto.DeviceType, Is.EqualTo(DeviceType.Mobile),
                "deviceType travels as a name, not an ordinal - the clients send \"Mobile\".");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GET .../certificate - the read side §H.4 verifies against
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetDeviceCertificate_ReturnsTheSignedWindowAsEpochSeconds()
    {
        var device = await SeedDevice(identityKey: [4, 5, 6]);
        var issued = DateTimeOffset.UtcNow.AddDays(-1);
        var expires = DateTimeOffset.UtcNow.AddDays(179);
        device.Certificate = PlausibleCertificate;
        device.CertificateIssuedAt = issued;
        device.CertificateExpiresAt = expires;
        device.CertificateIdentityKeyVersion = 2;
        await _context.SaveChangesAsync();

        var result = await MlsDeviceEndpoint.GetDeviceCertificate(UserId, ClientDeviceId, _context);
        var dto = ((Ok<DeviceCertificateDto>)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(dto.DeviceId, Is.EqualTo(ClientDeviceId));
            Assert.That(dto.Certificate, Is.EqualTo(PlausibleCertificate));
            Assert.That(dto.DeviceSignatureKey, Is.EqualTo(new byte[] { 4, 5, 6 }),
                "the certificate is worthless unless the verifier can tell which leaf it vouches for");
            Assert.That(dto.IdentityKeyVersion, Is.EqualTo(2));

            // Epoch seconds, because that is the form the signature was made over.
            Assert.That(dto.IssuedAt, Is.EqualTo(issued.ToUnixTimeSeconds()));
            Assert.That(dto.ExpiresAt, Is.EqualTo(expires.ToUnixTimeSeconds()));
        });
    }

    [Test]
    public async Task GetDeviceCertificate_ForADeviceWithoutOne_IsNotFound()
    {
        await SeedDevice();

        var result = await MlsDeviceEndpoint.GetDeviceCertificate(UserId, ClientDeviceId, _context);

        // Which the clients already read as "this device has published none", per §I.2.
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetDeviceCertificate_ForAnUnknownDevice_IsNotFound()
    {
        await SeedDevice();

        var result = await MlsDeviceEndpoint.GetDeviceCertificate(UserId, "device-nobody-has", _context);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }
}
