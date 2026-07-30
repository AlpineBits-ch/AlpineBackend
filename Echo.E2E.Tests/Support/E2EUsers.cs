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
        Assert.That(register.IsSuccessStatusCode, Is.True,
            $"Register failed: {await register.Content.ReadAsStringAsync()}\n{identity.CapturedOutput}");
        var registerBody = await register.Content.ReadFromJsonAsync<JsonElement>();
        var userId = registerBody.GetProperty("userId").GetString()!;

        var tokenResponse = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = password,
                ["client_id"] = "echo",
            }));
        Assert.That(tokenResponse.IsSuccessStatusCode, Is.True,
            $"Token request failed: {await tokenResponse.Content.ReadAsStringAsync()}\n{identity.CapturedOutput}");
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = tokenBody.GetProperty("access_token").GetString()!;

        await WaitForSocialProfileAsync(stack, userId, token);

        return (userId, token);
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
