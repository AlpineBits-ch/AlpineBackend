using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Hosts;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Registers a real user against a running <see cref="EchoTestStack"/> and hands back the
/// credentials scenario tests need.
///
/// Crucially it does not return until Social has actually materialized that user's profile.
/// Registration only writes the Identity row and publishes UserCreatedEvent; the profile appears
/// later, once the gateway's UserRegistrationSaga has turned that event into
/// CreateUserProfileCommand and Social has handled it (the chain CrossServiceFlowTests proves).
/// Anything that resolves a user through Social - Guild's POST /api/v1/guilds asks it for the
/// owner's profile over the bus and 400s "User not found" when there isn't one yet, and the DM
/// flows read GET /api/v1/profiles/by-user/{id} - therefore races that broker round trip if it
/// runs immediately after registering. It usually wins on a developer machine and loses on a
/// loaded CI runner, which is exactly the shape of flake this waits out.
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
        await E2EAssert.SucceededAsync(register, identity, "Register failed");
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
        await E2EAssert.SucceededAsync(tokenResponse, identity, "Token request failed");
        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = tokenBody.GetProperty("access_token").GetString()!;

        await WaitForSocialProfileAsync(stack, userId, token);

        return (userId, token);
    }

    /// <summary>
    /// Polls Social's own read model for the profile. Asking Social over HTTP (rather than
    /// querying its Postgres directly, as CrossServiceFlowTests does) keeps this independent of
    /// which per-stack database suffix the caller happens to be running under, and hits exactly
    /// the same rows the bus handler Guild calls into reads - so a 200 here means the next
    /// GetProfileByUserIdRequest will resolve too (that handler caches hits but never misses).
    /// </summary>
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
