using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Consumers;

/// <summary>The non-consuming device key directory.</summary>
[TestFixture]
public class GetUserDeviceKeysHandlerTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private UserDevice SeedDevice(
        string userId,
        string clientDeviceId,
        byte[]? publicKey = null,
        byte[]? certificate = null,
        DateTimeOffset? certificateExpiresAt = null,
        DeviceStatus status = DeviceStatus.Active)
    {
        var device = UserDevice.Create(new CreateUserDeviceParams
        {
            UserId = userId,
            ClientDeviceId = clientDeviceId,
            DeviceName = clientDeviceId,
            DeviceType = DeviceType.Desktop,
            IdentityPublicKey = publicKey ?? [0xAA, 0xBB, 0xCC],
        });

        device.Status = status;

        if (certificate is not null)
        {
            device.Certificate = certificate;
            device.CertificateIssuedAt = DateTimeOffset.UtcNow.AddDays(-1);
            device.CertificateExpiresAt = certificateExpiresAt ?? DateTimeOffset.UtcNow.AddDays(180);
            device.CertificateIdentityKeyVersion = 3;
        }

        _context.UserDevices.Add(device);
        return device;
    }

    /// <summary>A well-formed MLS10 + ciphersuite-1 header, because <see cref="UserKeyPackage.Create"/>
    /// derives the ciphersuite from bytes 2-3.</summary>
    private void SeedKeyPackage(UserDevice device, byte tag)
    {
        _context.UserKeyPackages.Add(UserKeyPackage.Create(new CreateUserKeyPackageParams
        {
            UserId = device.UserId,
            DeviceId = device.Id,
            KeyPackage = [0x00, 0x01, 0x00, 0x01, tag],
            IsLastResort = false,
        }));
    }

    private void SeedRevocation(string userId, string clientDeviceId, byte[] certificate,
        DateTimeOffset? revokedAt = null, string? reason = null)
    {
        _context.RevokedDeviceCertificates.Add(RevokedDeviceCertificate.Create(
            new CreateRevokedDeviceCertificateParams
            {
                UserId = userId,
                ClientDeviceId = clientDeviceId,
                Certificate = certificate,
                IdentityKeyVersion = 3,
                Reason = reason ?? CertificateRevocationReasons.DeviceRemoved,
                RevokedAt = revokedAt ?? DateTimeOffset.UtcNow.AddHours(-2),
            }));
    }

    private Task<GetUserDeviceKeysResponse> Ask(params string[] userIds) =>
        GetUserDeviceKeysHandler.Handle(new GetUserDeviceKeysRequest { UserIds = userIds }, _context);

    // ══════════════════════════════════════════════════════════════════════════
    // The regression this contract exists to prevent
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_DoesNotConsumeAnything()
    {
        // The payments page re-reads this on every render.
        var device = SeedDevice("user-1", "device-1");
        SeedKeyPackage(device, 1);
        SeedKeyPackage(device, 2);
        await _context.SaveChangesAsync();

        await Ask("user-1");
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _context.UserKeyPackages.CountAsync(p => p.ConsumedAt != null), Is.Zero,
                "A directory read must never stamp a single-use key package consumed");
            Assert.That(await _context.UserKeyPackages.CountAsync(), Is.EqualTo(2),
                "Nor delete one, which would be the same bug wearing a different hat");
        });
    }

    [Test]
    public async Task Handle_LeavesNothingPendingOnTheChangeTracker()
    {
        // Belt and braces on the same rule, and it catches the wider version: an AsNoTracking read
        // cannot mutate anything, so a future edit that starts tracking entities to reach a field
        // is caught here rather than at whatever the next SaveChanges happens to be.
        var device = SeedDevice("user-1", "device-1");
        SeedKeyPackage(device, 1);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await Ask("user-1");

        Assert.That(_context.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged),
            Is.Empty);
    }

    [Test]
    public async Task Handle_ReturnsTheLongTermIdentityKey_NotAKeyPackage()
    {
        // An MLS KeyPackage init key is one-time-use by design; sealing to one repeatedly is a
        // misuse of it and would break the moment the package was consumed elsewhere.
        var device = SeedDevice("user-1", "device-1", publicKey: [0x11, 0x22, 0x33]);
        SeedKeyPackage(device, 9);
        await _context.SaveChangesAsync();

        var response = await Ask("user-1");

        Assert.That(response.Devices.Single().PublicKey, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33 }));
    }

    // ══════════════════════════════════════════════════════════════════════════ Normal
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_UserWithTwoDevices_ReturnsBothKeys()
    {
        // Desktop plus phone is the ordinary case for anybody who has used the product for a week.
        SeedDevice("user-1", "device-phone", publicKey: [1]);
        SeedDevice("user-1", "device-desktop", publicKey: [2]);
        await _context.SaveChangesAsync();

        var response = await Ask("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(response.Devices.Select(d => d.DeviceId),
                Is.EquivalentTo(new[] { "device-phone", "device-desktop" }));
            Assert.That(response.Devices.Select(d => d.PublicKey),
                Is.EquivalentTo(new[] { new byte[] { 1 }, new byte[] { 2 } }));
        });
    }

    [Test]
    public async Task Handle_MultipleUsers_ReturnsEachUsersDevices()
    {
        SeedDevice("user-1", "device-a");
        SeedDevice("user-2", "device-b");
        await _context.SaveChangesAsync();

        var response = await Ask("user-1", "user-2");

        Assert.That(response.Devices.Select(d => (d.UserId, d.DeviceId)),
            Is.EquivalentTo(new[] { ("user-1", "device-a"), ("user-2", "device-b") }));
    }

    [Test]
    public async Task Handle_ReturnsTheClientDeviceIdNotTheRowId()
    {
        // This is what a wrap is addressed to; the row id means nothing outside Identity.
        SeedDevice("user-1", "client-device-abc");
        await _context.SaveChangesAsync();

        var response = await Ask("user-1");

        Assert.That(response.Devices.Single().DeviceId, Is.EqualTo("client-device-abc"));
    }

    [Test]
    public async Task Handle_CarriesTheCertificateAlongsideTheKey()
    {
        // Fetched separately there is a window in which the server could pair one device's key with
        // another's certificate, which is the substitution the certificate is supposed to rule out.
        SeedDevice("user-1", "device-1", certificate: [0xDE, 0xAD]);
        await _context.SaveChangesAsync();

        var device = (await Ask("user-1")).Devices.Single();

        Assert.Multiple(() =>
        {
            Assert.That(device.HasValidCertificate, Is.True);
            Assert.That(device.Certificate, Is.EqualTo(new byte[] { 0xDE, 0xAD }));
            Assert.That(device.CertificateIdentityKeyVersion, Is.EqualTo(3));
            Assert.That(device.CertificateExpiresAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Handle_DuplicateUserIds_DoNotDuplicateDevices()
    {
        SeedDevice("user-1", "device-1");
        await _context.SaveChangesAsync();

        var response = await Ask("user-1", "user-1");

        Assert.That(response.Devices, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════ Flagged, never
    // dropped ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_RevokedCertificate_IsReturnedAndFlagged()
    {
        // The failure being prevented: a caller that silently receives fewer devices than the
        // account owns cannot tell "Ben has one phone" from "somebody removed a device from the
        // response".
        var certificate = new byte[] { 0xBA, 0xD0 };
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-3);
        SeedDevice("user-1", "device-good", certificate: [0x01]);
        SeedDevice("user-1", "device-revoked", certificate: certificate);
        SeedRevocation("user-1", "device-revoked", certificate, revokedAt);
        await _context.SaveChangesAsync();

        var response = await Ask("user-1");

        var revoked = response.Devices.Single(d => d.DeviceId == "device-revoked");
        var good = response.Devices.Single(d => d.DeviceId == "device-good");
        Assert.Multiple(() =>
        {
            Assert.That(response.Devices, Has.Count.EqualTo(2), "The revoked device must still be listed");
            Assert.That(revoked.CertificateRevokedAt, Is.EqualTo(revokedAt).Within(TimeSpan.FromSeconds(1)));
            Assert.That(revoked.PublicKey, Is.Not.Null, "Still carries its key - the caller decides, not the server");
            Assert.That(good.CertificateRevokedAt, Is.Null);
        });
    }

    [Test]
    public async Task Handle_ReissuedCertificate_DoesNotFlagTheCurrentOne()
    {
        // Reissuing revokes the superseded certificate, so a revocation row exists for this device
        // in the ordinary healthy case.
        SeedDevice("user-1", "device-1", certificate: [0x02, 0x02]);
        SeedRevocation("user-1", "device-1", [0x01, 0x01], reason: CertificateRevocationReasons.Reissued);
        await _context.SaveChangesAsync();

        var device = (await Ask("user-1")).Devices.Single();

        Assert.That(device.CertificateRevokedAt, Is.Null);
    }

    [Test]
    public async Task Handle_DeviceWithNoCertificate_IsReturnedUnattested()
    {
        // A device that has never published a certificate is exactly the one a strict client wants
        // to refuse.
        SeedDevice("user-1", "device-bare");
        await _context.SaveChangesAsync();

        var device = (await Ask("user-1")).Devices.Single();

        Assert.Multiple(() =>
        {
            Assert.That(device.HasValidCertificate, Is.False);
            Assert.That(device.Certificate, Is.Null);
            Assert.That(device.CertificateRevokedAt, Is.Null, "Absent is not the same as repudiated");
        });
    }

    [Test]
    public async Task Handle_ExpiredCertificate_IsReturnedWithTheFlagOff()
    {
        SeedDevice("user-1", "device-1", certificate: [0x03],
            certificateExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        await _context.SaveChangesAsync();

        var device = (await Ask("user-1")).Devices.Single();

        Assert.Multiple(() =>
        {
            Assert.That(device.HasValidCertificate, Is.False);
            Assert.That(device.DeviceId, Is.EqualTo("device-1"));
        });
    }

    [Test]
    public async Task Handle_RemovedDevice_IsReturnedAsInactive()
    {
        // Unlike GetUserDevicesHandler, which filters to Active.
        SeedDevice("user-1", "device-live");
        SeedDevice("user-1", "device-gone", status: DeviceStatus.Removed);
        await _context.SaveChangesAsync();

        var response = await Ask("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(response.Devices, Has.Count.EqualTo(2));
            Assert.That(response.Devices.Single(d => d.DeviceId == "device-gone").IsActive, Is.False);
            Assert.That(response.Devices.Single(d => d.DeviceId == "device-live").IsActive, Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Edge and negative
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_UserWithNoDevices_IsSimplyAbsent()
    {
        SeedDevice("user-1", "device-1");
        await _context.SaveChangesAsync();

        var response = await Ask("user-1", "user-without-devices");

        Assert.Multiple(() =>
        {
            Assert.That(response.Devices.Select(d => d.UserId), Is.EquivalentTo(new[] { "user-1" }));
            Assert.That(response.OmittedUserIds, Is.Empty,
                "Nothing was truncated - the account genuinely has no devices");
        });
    }

    [Test]
    public async Task Handle_UnknownUserId_IsAbsentRatherThanAnError()
    {
        // "No such account" and "that account has no devices" are the same answer to this question.
        SeedDevice("user-1", "device-1");
        await _context.SaveChangesAsync();

        var response = await Ask("user-1", "user-does-not-exist");

        Assert.Multiple(() =>
        {
            Assert.That(response.Devices, Has.Count.EqualTo(1));
            Assert.That(response.OmittedUserIds, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_NoUserIds_ReturnsEmpty()
    {
        var response = await Ask();

        Assert.Multiple(() =>
        {
            Assert.That(response.Devices, Is.Empty);
            Assert.That(response.OmittedUserIds, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_BlankUserIds_AreIgnored()
    {
        SeedDevice("user-1", "device-1");
        await _context.SaveChangesAsync();

        var response = await Ask("user-1", "", "   ");

        Assert.That(response.Devices, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Handle_OverTheBatchCap_TruncatesAndSaysSo()
    {
        // The cap is a guard against an unbounded caller turning a key lookup into a table scan,
        // not a paging mechanism.
        SeedDevice("user-1", "device-1");
        await _context.SaveChangesAsync();

        var ids = new[] { "user-1" }
            .Concat(Enumerable.Range(0, GetUserDeviceKeysRequest.MaxUserIds + 9).Select(i => $"filler-{i}"))
            .ToArray();

        var response = await Ask(ids);

        Assert.Multiple(() =>
        {
            Assert.That(response.OmittedUserIds, Has.Count.EqualTo(10));
            Assert.That(response.OmittedUserIds, Is.EquivalentTo(ids.Skip(GetUserDeviceKeysRequest.MaxUserIds)));
            Assert.That(response.Devices.Select(d => d.DeviceId), Is.EquivalentTo(new[] { "device-1" }),
                "Ids within the cap are still answered in full");
        });
    }

    [Test]
    public async Task Handle_AtExactlyTheBatchCap_OmitsNothing()
    {
        SeedDevice("user-1", "device-1");
        await _context.SaveChangesAsync();

        var ids = new[] { "user-1" }
            .Concat(Enumerable.Range(0, GetUserDeviceKeysRequest.MaxUserIds - 1).Select(i => $"filler-{i}"))
            .ToArray();

        var response = await Ask(ids);

        Assert.That(response.OmittedUserIds, Is.Empty);
    }
}
