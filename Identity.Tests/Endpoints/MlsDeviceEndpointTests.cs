using Domain;
using Identity.Application.Dtos.Request;
using Identity.Application.Dtos.Response;
using Identity.Application.Endpoints;
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

    private TestIdentityContext _context = null!;
    private MlsDeviceEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestIdentityContext(Guid.NewGuid().ToString());
        _endpoint = new MlsDeviceEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

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
            ClientDeviceId, TestPrincipal.ForUser(UserId), _context);
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

        await MlsDeviceEndpoint.ResetKeyPackages(ClientDeviceId, TestPrincipal.ForUser(UserId), _context);
        await _context.SaveChangesAsync();
        var second = await MlsDeviceEndpoint.ResetKeyPackages(ClientDeviceId, TestPrincipal.ForUser(UserId), _context);

        Assert.That(((Ok<ResetKeyPackagesResultDto>)second).Value!.DeletedCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ResetKeyPackages_ForAnUnknownDevice_IsNotFound()
    {
        await SeedDevice();

        var result = await MlsDeviceEndpoint.ResetKeyPackages(
            "device-nobody-has", TestPrincipal.ForUser(UserId), _context);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ResetKeyPackages_ForAnotherUsersDevice_IsForbidden()
    {
        var other = await SeedDevice("user-2", "device-theirs");
        await SeedDevice();
        await SeedKeyPackages(other.Id, count: 2);

        var result = await MlsDeviceEndpoint.ResetKeyPackages(
            "device-theirs", TestPrincipal.ForUser(UserId), _context);
        await _context.SaveChangesAsync();

        // ClientDeviceId is only unique per user, so "signed in as the wrong account" is a real and
        // distinguishable case - and it is the mistake that actually happens.
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(await _context.UserKeyPackages.CountAsync(), Is.EqualTo(3));
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
            RegisterDto([1, 2, 3]), new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);
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

        var (result, evt) = await _endpoint.CreateDevice(
            RegisterDto([9, 9, 9]), new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);
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
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);
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
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);
        await _context.SaveChangesAsync();

        await _endpoint.CreateDevice(
            RegisterDto(capabilities: null), new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);
        await _context.SaveChangesAsync();

        // Absent means "told us nothing", not "supports nothing" - a partial re-registration must
        // not erase a device's declared support and quietly hold the account out of the strict tier.
        Assert.That((await _context.UserDevices.SingleAsync()).Capabilities,
            Is.EquivalentTo(new[] { MlsCapabilities.DeviceCertificateV1 }));
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
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);
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
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);

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
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);

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
            new FakeIdentityMessageBus(), TestPrincipal.ForUser(UserId), _context);

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
}
