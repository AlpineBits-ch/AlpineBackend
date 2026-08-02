using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Consumers;

/// <summary>
/// Covers key-package handout: one package per active device, single-use before last-resort,
/// expiry, and the multi-device case that used to throw.
/// </summary>
[TestFixture]
public class ConsumeMlsTokensForUserHandlerTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>ProtocolVersion=MLS10 (0x0001), CipherSuite=1 (0x0001), then a payload byte so the
    /// packages are distinguishable from each other.</summary>
    private static byte[] KeyPackageBytes(byte tag) => [0x00, 0x01, 0x00, 0x01, tag];

    private UserDevice SeedDevice(string userId, string clientDeviceId, DeviceStatus status = DeviceStatus.Active)
    {
        var device = UserDevice.Create(new CreateUserDeviceParams
        {
            UserId = userId,
            ClientDeviceId = clientDeviceId,
            DeviceName = clientDeviceId,
            DeviceType = DeviceType.Desktop,
            IdentityPublicKey = [1, 2, 3],
        });
        device.Status = status;
        _context.UserDevices.Add(device);
        return device;
    }

    private UserKeyPackage SeedPackage(
        UserDevice device,
        byte tag,
        bool isLastResort = false,
        DateTime? expiresAt = null,
        DateTime? consumedAt = null,
        DateTimeOffset? createdAt = null)
    {
        var package = UserKeyPackage.Create(new CreateUserKeyPackageParams
        {
            UserId = device.UserId,
            DeviceId = device.Id,
            KeyPackage = KeyPackageBytes(tag),
            IsLastResort = isLastResort,
            ExpiresAt = expiresAt,
        });
        package.ConsumedAt = consumedAt;
        if (createdAt is not null) package.CreatedAt = createdAt.Value;
        _context.UserKeyPackages.Add(package);
        return package;
    }

    private Task<Identity.Contracts.Bus.Response.ConsumeMlsDeviceTokensForUserResponse> Consume(params string[] userIds) =>
        ConsumeMlsTokensForUserHandler.Handle(
            new ConsumeMlsDeviceTokensForUserRequest { UserIds = userIds }, _context);

    // ══════════════════════════════════════════════════════════════════════════ The multi-device
    // crash ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_UserWithTwoDevices_ReturnsOneTokenPerDevice()
    {
        // The previous shape appended the whole per-user device list once per device that had a
        // token, so two devices produced duplicates and the later Single(...) lookup threw.
        var phone = SeedDevice("user-1", "device-phone");
        var desktop = SeedDevice("user-1", "device-desktop");
        SeedPackage(phone, 1);
        SeedPackage(desktop, 2);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        Assert.That(response.DeviceTokens.Select(t => t.DeviceId),
            Is.EquivalentTo(new[] { "device-phone", "device-desktop" }));
    }

    [Test]
    public async Task Handle_TwoDevicesOneOutOfKeyPackages_AnswersWithBothAToken_AndAnUnreachable()
    {
        // The exact shape of the live report: one account, phone and desktop, desktop out of key
        // packages.
        var phone = SeedDevice("user-1", "device-phone");
        SeedDevice("user-1", "device-desktop");
        SeedPackage(phone, 1);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(response.DeviceTokens.Select(t => t.DeviceId), Is.EquivalentTo(new[] { "device-phone" }));
            Assert.That(response.UnreachableDevices.Select(d => d.DeviceId), Is.EquivalentTo(new[] { "device-desktop" }));
        });
    }

    [Test]
    public async Task Handle_ThreeDevices_StillReturnsOneEach()
    {
        var devices = new[] { "d-1", "d-2", "d-3" }.Select(id => SeedDevice("user-1", id)).ToList();
        for (var i = 0; i < devices.Count; i++) SeedPackage(devices[i], (byte)i);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        Assert.That(response.DeviceTokens, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Handle_ReturnsTheClientDeviceIdNotTheRowId()
    {
        var device = SeedDevice("user-1", "client-device-abc");
        SeedPackage(device, 1);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        // This is what the caller addresses Welcomes and per-device pushes to; the row id is
        // meaningless outside this service.
        Assert.That(response.DeviceTokens.Single().DeviceId, Is.EqualTo("client-device-abc"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Selection order
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_ConsumesTheOldestUnconsumedPackageFirst()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 2, createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        SeedPackage(device, 1, createdAt: DateTimeOffset.UtcNow.AddDays(-5));
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        Assert.That(response.DeviceTokens.Single().Token, Is.EqualTo(KeyPackageBytes(1)),
            "Draining in upload order stops a stale tail accumulating");
    }

    [Test]
    public async Task Handle_MarksTheHandedOutPackageConsumed()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 1);
        SeedPackage(device, 2);
        await _context.SaveChangesAsync();

        await Consume("user-1");
        await _context.SaveChangesAsync();

        Assert.That(await _context.UserKeyPackages.CountAsync(p => p.ConsumedAt != null), Is.EqualTo(1));
    }

    [Test]
    public async Task Handle_NeverHandsOutTheSamePackageTwice()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 1);
        SeedPackage(device, 2);
        await _context.SaveChangesAsync();

        var first = (await Consume("user-1")).DeviceTokens.Single().Token;
        await _context.SaveChangesAsync();
        var second = (await Consume("user-1")).DeviceTokens.Single().Token;

        Assert.That(second, Is.Not.EqualTo(first));
    }

    [Test]
    public async Task Handle_SkipsExpiredPackages()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 1, expiresAt: DateTime.UtcNow.AddDays(-1));
        SeedPackage(device, 2, expiresAt: DateTime.UtcNow.AddDays(30));
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        // Handing out an expired package fails validation inside the inviter's MLS library, which
        // fails the add for the whole group rather than just for this device.
        Assert.That(response.DeviceTokens.Single().Token, Is.EqualTo(KeyPackageBytes(2)));
    }

    [Test]
    public async Task Handle_AllPackagesExpired_ReportsDeviceUnreachable()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 1, expiresAt: DateTime.UtcNow.AddDays(-1));
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(response.DeviceTokens, Is.Empty);
            Assert.That(response.UnreachableDevices.Single().DeviceId, Is.EqualTo("device-1"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Last resort
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_FallsBackToLastResortWhenSingleUseSupplyIsDrained()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 9, isLastResort: true);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        // Without this the device is silently skipped and ends up in a conversation it can never
        // read, with no error surfaced to anyone.
        Assert.That(response.DeviceTokens.Single().Token, Is.EqualTo(KeyPackageBytes(9)));
        Assert.That(response.UnreachableDevices, Is.Empty);
    }

    [Test]
    public async Task Handle_PrefersSingleUseOverLastResort()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 9, isLastResort: true);
        SeedPackage(device, 1);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        // Reuse costs forward secrecy, so the last-resort package is a floor and not a substitute.
        Assert.That(response.DeviceTokens.Single().Token, Is.EqualTo(KeyPackageBytes(1)));
    }

    [Test]
    public async Task Handle_LastResortIsReusable()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 9, isLastResort: true);
        await _context.SaveChangesAsync();

        await Consume("user-1");
        await _context.SaveChangesAsync();
        var second = await Consume("user-1");

        Assert.That(second.DeviceTokens.Single().Token, Is.EqualTo(KeyPackageBytes(9)));
        Assert.That(await _context.UserKeyPackages.SingleAsync(), Has.Property("ConsumedAt").Null);
    }

    [Test]
    public async Task Handle_SkipsExpiredLastResort()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 9, isLastResort: true, expiresAt: DateTime.UtcNow.AddDays(-1));
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        Assert.That(response.UnreachableDevices.Single().DeviceId, Is.EqualTo("device-1"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Scope and edge
    // cases ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handle_IgnoresRemovedDevices()
    {
        var removed = SeedDevice("user-1", "device-removed", DeviceStatus.Removed);
        SeedPackage(removed, 1);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(response.DeviceTokens, Is.Empty);
            Assert.That(response.UnreachableDevices, Is.Empty, "A removed device is not expected in the group at all");
        });
    }

    [Test]
    public async Task Handle_DeviceWithNoPackagesAtAll_IsReportedUnreachable()
    {
        SeedDevice("user-1", "device-empty");
        await _context.SaveChangesAsync();

        var response = await Consume("user-1");

        var unreachable = response.UnreachableDevices.Single();
        Assert.Multiple(() =>
        {
            Assert.That(unreachable.DeviceId, Is.EqualTo("device-empty"));
            Assert.That(unreachable.UserId, Is.EqualTo("user-1"));
            Assert.That(unreachable.DeviceName, Is.EqualTo("device-empty"));
        });
    }

    [Test]
    public async Task Handle_MultipleUsers_ReturnsTokensForEach()
    {
        var a = SeedDevice("user-1", "device-a");
        var b = SeedDevice("user-2", "device-b");
        SeedPackage(a, 1);
        SeedPackage(b, 2);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1", "user-2");

        Assert.That(response.DeviceTokens.Select(t => t.UserId), Is.EquivalentTo(new[] { "user-1", "user-2" }));
    }

    [Test]
    public async Task Handle_DuplicateUserIds_DoNotDoubleConsume()
    {
        var device = SeedDevice("user-1", "device-1");
        SeedPackage(device, 1);
        SeedPackage(device, 2);
        await _context.SaveChangesAsync();

        var response = await Consume("user-1", "user-1");
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(response.DeviceTokens, Has.Count.EqualTo(1));
            Assert.That(await _context.UserKeyPackages.CountAsync(p => p.ConsumedAt != null), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Handle_NoUserIds_ReturnsEmpty()
    {
        var response = await Consume();

        Assert.That(response.DeviceTokens, Is.Empty);
        Assert.That(response.UnreachableDevices, Is.Empty);
    }
}
