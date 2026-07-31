using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;

namespace Identity.Tests.Consumers;

/// <summary>
/// Backs the X-Device-Id check that Messaging and Guild run before keying call/voice state on a
/// caller-supplied device id.
/// </summary>
[TestFixture]
public class ValidateUserDeviceHandlerTests
{
    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private void SeedDevice(string userId, string clientDeviceId, DeviceStatus status = DeviceStatus.Active)
    {
        var device = UserDevice.Create(new CreateUserDeviceParams
        {
            UserId = userId,
            ClientDeviceId = clientDeviceId,
            DeviceName = "Test device",
            DeviceType = DeviceType.Desktop,
            IdentityPublicKey = [1, 2, 3],
        });
        device.Status = status;
        _context.UserDevices.Add(device);
    }

    private Task<Identity.Contracts.Bus.Response.ValidateUserDeviceResponse> Validate(string userId, string deviceId) =>
        ValidateUserDeviceHandler.Handle(
            new ValidateUserDeviceRequest { UserId = userId, ClientDeviceId = deviceId }, _context);

    [Test]
    public async Task OwnActiveDevice_IsRegistered()
    {
        SeedDevice("user-1", "desktop-1");
        await _context.SaveChangesAsync();

        var response = await Validate("user-1", "desktop-1");

        Assert.Multiple(() =>
        {
            Assert.That(response.IsRegistered, Is.True);
            Assert.That(response.DeviceName, Is.EqualTo("Test device"));
        });
    }

    [Test]
    public async Task AnotherUsersDevice_IsNotRegistered()
    {
        SeedDevice("user-2", "desktop-1");
        await _context.SaveChangesAsync();

        var response = await Validate("user-1", "desktop-1");

        Assert.That(response.IsRegistered, Is.False);
    }

    [Test]
    public async Task RemovedDevice_IsNotRegistered()
    {
        SeedDevice("user-1", "desktop-1", DeviceStatus.Removed);
        await _context.SaveChangesAsync();

        var response = await Validate("user-1", "desktop-1");

        Assert.That(response.IsRegistered, Is.False);
    }

    [Test]
    public async Task UnknownDevice_IsNotRegistered()
    {
        var response = await Validate("user-1", "never-seen");

        Assert.That(response.IsRegistered, Is.False);
    }

    [Test]
    public async Task BlankInput_IsNotRegistered()
    {
        var response = await Validate("user-1", "   ");

        Assert.That(response.IsRegistered, Is.False);
    }
}
