using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using ContractKind = Identity.Contracts.Enums.PushTokenKind;

namespace Identity.Tests.Consumers;

/// <summary>
/// The single push-endpoint lookup that replaced GetDeviceTokenHandler + GetVoipTokenHandler.
/// Every sender used to invoke both and get back bare strings, so neither the transport nor the
/// owning device survived the trip.
/// </summary>
[TestFixture]
public class GetPushTokensHandlerTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private UserDevice SeedDevice(string userId, string clientDeviceId)
    {
        var device = UserDevice.Create(new CreateUserDeviceParams
        {
            UserId = userId,
            ClientDeviceId = clientDeviceId,
            DeviceName = clientDeviceId,
            DeviceType = DeviceType.Mobile,
            IdentityPublicKey = [1, 2, 3],
        });
        _context.UserDevices.Add(device);
        return device;
    }

    private void SeedToken(string userId, string token, PushTokenKind kind, UserDevice? device = null)
    {
        _context.UserPushTokens.Add(UserPushToken.Create(new CreateUserPushTokenParams
        {
            UserId = userId,
            Token = token,
            Kind = kind,
            DeviceId = device?.Id,
        }));
    }

    private Task<Identity.Contracts.Bus.Response.GetPushTokensForUsersResponse> Get(
        IEnumerable<string> userIds,
        IEnumerable<ContractKind>? kinds = null,
        IEnumerable<string>? excludeDevices = null) =>
        GetPushTokensHandler.Handle(new GetPushTokensForUsersRequest
        {
            UserIds = userIds.ToList(),
            Kinds = kinds?.ToList() ?? [],
            ExcludeClientDeviceIds = excludeDevices?.ToList() ?? [],
        }, _context);

    [Test]
    public async Task ReturnsBothTransportsInOneCall()
    {
        SeedToken("user-1", "fcm-token", PushTokenKind.Fcm);
        SeedToken("user-1", "voip-token", PushTokenKind.ApnsVoip);
        await _context.SaveChangesAsync();

        var response = await Get(["user-1"]);

        Assert.Multiple(() =>
        {
            Assert.That(response.Of(ContractKind.Fcm), Is.EquivalentTo(new[] { "fcm-token" }));
            Assert.That(response.Of(ContractKind.ApnsVoip), Is.EquivalentTo(new[] { "voip-token" }));
        });
    }

    [Test]
    public async Task KindFilter_LimitsToTheRequestedTransport()
    {
        SeedToken("user-1", "fcm-token", PushTokenKind.Fcm);
        SeedToken("user-1", "voip-token", PushTokenKind.ApnsVoip);
        await _context.SaveChangesAsync();

        var response = await Get(["user-1"], kinds: [ContractKind.Fcm]);

        Assert.That(response.Tokens.Select(t => t.Token), Is.EquivalentTo(new[] { "fcm-token" }));
    }

    [Test]
    public async Task ExcludedDevice_IsLeftOut_ButOtherDevicesOfTheSameUserAreNot()
    {
        // The point of the device link: the device that just accepted a call must not get the
        // "call cancelled" push, while the user's other ringing handsets must.
        var accepting = SeedDevice("user-1", "desktop-1");
        var other = SeedDevice("user-1", "phone-1");
        SeedToken("user-1", "desktop-token", PushTokenKind.Fcm, accepting);
        SeedToken("user-1", "phone-token", PushTokenKind.Fcm, other);
        await _context.SaveChangesAsync();

        var response = await Get(["user-1"], excludeDevices: ["desktop-1"]);

        Assert.That(response.Of(ContractKind.Fcm), Is.EquivalentTo(new[] { "phone-token" }));
    }

    [Test]
    public async Task UnattachedTokens_AreNeverExcluded()
    {
        // Nothing says which installation an unattached token belongs to, so dropping it on an
        // exclusion would silently stop delivering to a device that should have been notified.
        var accepting = SeedDevice("user-1", "desktop-1");
        SeedToken("user-1", "desktop-token", PushTokenKind.Fcm, accepting);
        SeedToken("user-1", "legacy-token", PushTokenKind.Fcm);
        await _context.SaveChangesAsync();

        var response = await Get(["user-1"], excludeDevices: ["desktop-1"]);

        Assert.That(response.Of(ContractKind.Fcm), Is.EquivalentTo(new[] { "legacy-token" }));
    }

    [Test]
    public async Task ResponseCarriesTheClientDeviceId_NotTheRowId()
    {
        var device = SeedDevice("user-1", "phone-1");
        SeedToken("user-1", "phone-token", PushTokenKind.Fcm, device);
        await _context.SaveChangesAsync();

        var response = await Get(["user-1"]);

        Assert.That(response.Tokens.Single().ClientDeviceId, Is.EqualTo("phone-1"));
    }

    [Test]
    public async Task OtherUsersTokens_AreNotReturned()
    {
        SeedToken("user-1", "mine", PushTokenKind.Fcm);
        SeedToken("user-2", "theirs", PushTokenKind.Fcm);
        await _context.SaveChangesAsync();

        var response = await Get(["user-1"]);

        Assert.That(response.Tokens.Select(t => t.Token), Is.EquivalentTo(new[] { "mine" }));
    }

    [Test]
    public async Task NoUserIds_ShortCircuits()
    {
        SeedToken("user-1", "mine", PushTokenKind.Fcm);
        await _context.SaveChangesAsync();

        var response = await Get([]);

        Assert.That(response.Tokens, Is.Empty);
    }
}
