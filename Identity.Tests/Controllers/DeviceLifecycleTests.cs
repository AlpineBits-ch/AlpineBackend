using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>
/// End-to-end cover for the consolidated device story: registering a device, attaching a push token
/// to it, unregistering the device, and the cross-user collision that used to delete somebody
/// else's device row.
/// </summary>
[TestFixture]
public class DeviceLifecycleTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<string> RegisterAndLoginAsync(string username)
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var tokenResult = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        return (await tokenResult.ReadAsJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private static async Task RegisterDeviceAsync(string token, string clientDeviceId)
    {
        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreateMLSDeviceDto
            {
                ClientDeviceId = clientDeviceId,
                DeviceName = "Test device",
                DeviceType = DeviceType.Desktop,
                IdentityPublicKey = [1, 2, 3],
            }).ToUrl("/api/v1/devices");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
    }

    [Test]
    public async Task RegisterPushToken_WithDeviceId_AttachesItToThatDevice()
    {
        var token = await RegisterAndLoginAsync($"devpush{Guid.NewGuid():N}"[..15]);
        await RegisterDeviceAsync(token, "desktop-1");

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreatePushTokenDto
            {
                Token = "fcm-abc",
                Kind = PushTokenKind.Fcm,
                DeviceId = "desktop-1",
            }).ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var stored = await ctx.UserPushTokens.Include(t => t.Device).SingleAsync(t => t.Token == "fcm-abc");

        Assert.Multiple(() =>
        {
            Assert.That(stored.Kind, Is.EqualTo(PushTokenKind.Fcm));
            Assert.That(stored.Device!.ClientDeviceId, Is.EqualTo("desktop-1"));
        });
    }

    [Test]
    public async Task LegacyVoipEndpoint_StillRegisters_AsAnApnsVoipToken()
    {
        // Builds already in the wild keep posting to the old per-transport routes.
        var token = await RegisterAndLoginAsync($"devlegacy{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreateDeviceTokenDto { Token = "voip-abc" }).ToUrl("/api/v1/users/self/voip-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var stored = await ctx.UserPushTokens.SingleAsync(t => t.Token == "voip-abc");

        Assert.That(stored.Kind, Is.EqualTo(PushTokenKind.ApnsVoip));
    }

    [Test]
    public async Task RegisteringATokenAlreadyHeldByAnotherAccount_ReassignsIt()
    {
        // Both push providers hand the same token to a different account after a reinstall or an
        // account switch on the same handset; the previous owner must stop receiving that
        // handset's notifications.
        var firstOwner = await RegisterAndLoginAsync($"devfirst{Guid.NewGuid():N}"[..15]);
        var secondOwner = await RegisterAndLoginAsync($"devsecond{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(firstOwner);
            x.Post.Json(new CreatePushTokenDto { Token = "recycled", Kind = PushTokenKind.Fcm })
                .ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(secondOwner);
            x.Post.Json(new CreatePushTokenDto { Token = "recycled", Kind = PushTokenKind.Fcm })
                .ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var rows = await ctx.UserPushTokens.Where(t => t.Token == "recycled").ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1), "the token must not exist twice");
    }

    [Test]
    public async Task RemoveDevice_DeletesItAndItsPushTokens()
    {
        var token = await RegisterAndLoginAsync($"devremove{Guid.NewGuid():N}"[..15]);
        await RegisterDeviceAsync(token, "desktop-1");

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreatePushTokenDto { Token = "fcm-gone", Kind = PushTokenKind.Fcm, DeviceId = "desktop-1" })
                .ToUrl("/api/v1/users/self/push-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Delete.Url("/api/v1/devices/client/desktop-1");
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        Assert.Multiple(async () =>
        {
            Assert.That(await ctx.UserDevices.AnyAsync(d => d.ClientDeviceId == "desktop-1"), Is.False);
            Assert.That(await ctx.UserPushTokens.AnyAsync(t => t.Token == "fcm-gone"), Is.False,
                "a removed device must not leave a token that keeps ringing the handset");
        });
    }

    [Test]
    public async Task RemoveDevice_SomeoneElsesDevice_IsNotFound()
    {
        var owner = await RegisterAndLoginAsync($"devowner{Guid.NewGuid():N}"[..15]);
        var other = await RegisterAndLoginAsync($"devother{Guid.NewGuid():N}"[..15]);
        await RegisterDeviceAsync(owner, "shared-id");

        await Host.Scenario(x =>
        {
            x.WithBearerToken(other);
            x.Delete.Url("/api/v1/devices/client/shared-id");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        Assert.That(await ctx.UserDevices.AnyAsync(d => d.ClientDeviceId == "shared-id"), Is.True);
    }

    [Test]
    public async Task TwoAccountsCanRegisterTheSameClientDeviceId_WithoutDestroyingEachOther()
    {
        // The whole point of scoping the uniqueness per user: registration used to "resolve" a
        // collision by deleting the other account's device row and cascading away its key
        // packages, so any account could wipe another's device by claiming its id.
        var first = await RegisterAndLoginAsync($"devcolla{Guid.NewGuid():N}"[..15]);
        var second = await RegisterAndLoginAsync($"devcollb{Guid.NewGuid():N}"[..15]);

        await RegisterDeviceAsync(first, "same-id");
        await RegisterDeviceAsync(second, "same-id");

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        Assert.That(await ctx.UserDevices.CountAsync(d => d.ClientDeviceId == "same-id"), Is.EqualTo(2));
    }

    [TearDown]
    public async Task ClearDatabaseAfterTest()
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        if (await ctx.Users.AnyAsync())
        {
            ctx.Users.RemoveRange(ctx.Users);
            await ctx.SaveChangesAsync();
        }
    }
}
