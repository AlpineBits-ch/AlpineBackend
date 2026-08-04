using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Hosts;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Registers a real user against a running <see cref="EchoTestStack"/> and hands back the
/// credentials scenario tests need.
/// </summary>
public static class E2EUsers
{
    private static readonly TimeSpan ProfileTimeout = TimeSpan.FromSeconds(30);

    public static async Task<(string userId, string token)> RegisterAndGetTokenAsync(
        EchoTestStack stack, string username)
    {
        var identity = stack.Identity;
        var email = $"{username}-{Guid.NewGuid()}@example.com";
        const string password = "SecurePass123!";

        var register = await identity.Client.PostAsJsonAsync("/api/v1/authentication/register", new
        {
            Email = email,
            Password = password,
            Username = username,
            BirthDate = DateTime.UtcNow.AddYears(-20),
        });
        // 202 with a fixed body, and no user id in it - registration answers a taken address
        // exactly as it answers a free one, so there is nothing account-specific left to return.
        await E2EAssert.HasStatusAsync(register, HttpStatusCode.Accepted, identity, "Register failed");

        var tokenResponse = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = password,
                ["client_id"] = "echo",
            }));
        await E2EAssert.SucceededAsync(tokenResponse, identity, "Token request failed");
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = tokenBody.GetProperty("access_token").GetString()!;
        var userId = UserIdFromAccessToken(token);

        await WaitForSocialProfileAsync(stack, userId, token);

        return (userId, token);
    }

    /// <summary>Reads the account id out of an access token's <c>sub</c> claim.</summary>
    public static string UserIdFromAccessToken(string accessToken)
    {
        var segments = accessToken.Split('.');
        Assert.That(segments, Has.Length.GreaterThanOrEqualTo(2), "access token is not a JWT");

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return document.RootElement.GetProperty("sub").GetString()!;
    }

    /// <summary>Polls Social's own read model for the profile.</summary>
    public static async Task WaitForSocialProfileAsync(EchoTestStack stack, string userId, string token)
    {
        using var client = new HttpClient { BaseAddress = stack.Social.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var deadline = DateTime.UtcNow + ProfileTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/v1/profiles/by-user/{userId}");
            if (response.IsSuccessStatusCode) return;
            await Task.Delay(200);
        }

        Assert.Fail(
            $"Social never materialized a profile for user {userId} within {ProfileTimeout} - the " +
            $"UserCreatedEvent -> UserRegistrationSaga -> CreateUserProfileCommand chain is broken, " +
            $"not merely slow.\n{stack.Social.CapturedOutput}");
    }
}
