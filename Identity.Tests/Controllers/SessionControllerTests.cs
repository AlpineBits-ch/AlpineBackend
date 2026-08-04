using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>Covers GET/DELETE api/v1/sessions - listing and revoking LoginSession rows created
/// by ConnectController.Exchange.</summary>
[TestFixture]
public class SessionControllerTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<string> RegisterAndLoginAsync(string username, bool offlineAccess = false)
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

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
        };
        if (offlineAccess) form["scope"] = "offline_access";

        var tokenResult = await Host.Scenario(x =>
        {
            x.Post.FormData(form).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await tokenResult.ReadAsJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    [Test]
    public async Task GetSessions_ReturnsCurrentSessionMarkedAsCurrent()
    {
        var username = $"sesslist{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/sessions");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        var sessions = body.EnumerateArray().ToList();
        Assert.That(sessions, Has.Count.EqualTo(1));
        Assert.That(sessions[0].GetProperty("isCurrent").GetBoolean(), Is.True);
        Assert.That(sessions[0].GetProperty("deviceName").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GetSessions_MultipleLogins_ListsAllNonRevoked()
    {
        var username = $"sessmulti{Guid.NewGuid():N}"[..15];
        var firstToken = await RegisterAndLoginAsync(username);

        // A second password-grant login for the same account creates a second, independent session.
        var secondTokenResult = await Host.Scenario(x =>
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
        var secondBody = await secondTokenResult.ReadAsJsonAsync<JsonElement>();
        var secondToken = secondBody.GetProperty("access_token").GetString()!;

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(firstToken);
            x.Get.Url("/api/v1/sessions");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.EnumerateArray().Count(), Is.EqualTo(2));

        // second token still usable, just proving both are independent live sessions
        await Host.Scenario(x =>
        {
            x.WithBearerToken(secondToken);
            x.Get.Url("/api/v1/sessions");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
    }

    [Test]
    public async Task GetSessions_WithoutBearerToken_ReturnsUnauthorized()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/sessions");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    [Test]
    public async Task DeleteSession_Owner_RevokesSession()
    {
        var username = $"sessrevoke{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username, offlineAccess: true);

        var listResult = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/sessions");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var sessionId = (await listResult.ReadAsJsonAsync<JsonElement>())[0].GetProperty("id").GetString();

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Delete.Url($"/api/v1/sessions/{sessionId}");
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });

        var afterRevoke = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/sessions");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var remaining = await afterRevoke.ReadAsJsonAsync<JsonElement>();
        Assert.That(remaining.EnumerateArray(), Is.Empty);
    }

    [Test]
    public async Task DeleteSession_NotOwner_ReturnsForbidden()
    {
        var ownerUsername = $"sessowner{Guid.NewGuid():N}"[..15];
        var ownerToken = await RegisterAndLoginAsync(ownerUsername);
        var attackerToken = await RegisterAndLoginAsync($"sessattacker{Guid.NewGuid():N}"[..15]);

        var listResult = await Host.Scenario(x =>
        {
            x.WithBearerToken(ownerToken);
            x.Get.Url("/api/v1/sessions");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var sessionId = (await listResult.ReadAsJsonAsync<JsonElement>())[0].GetProperty("id").GetString();

        await Host.Scenario(x =>
        {
            x.WithBearerToken(attackerToken);
            x.Delete.Url($"/api/v1/sessions/{sessionId}");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Test]
    public async Task DeleteSession_UnknownId_ReturnsNotFound()
    {
        var token = await RegisterAndLoginAsync($"sessunknown{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Delete.Url($"/api/v1/sessions/lgsn_{Guid.NewGuid():N}");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
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
