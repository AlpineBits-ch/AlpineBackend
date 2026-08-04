using System.Net;
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
        // 202 with a fixed body, and no user id in it - registration answers a taken address exactly
        // as it answers a free one, so there is nothing account-specific left to return. The id now
        // comes off the token below, which is where a real client gets it too.
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

    /// <summary>
    /// Reads the account id out of an access token's <c>sub</c> claim.
    ///
    /// <para>Registration used to hand the id back in its response body. It no longer can - the
    /// answer has to be identical for an address that already has an account, and an id is the one
    /// thing that cannot be - so this is the post-change way to find out who you just became, and
    /// it is what the real clients do (see docs/specs/registration-contract-change.md).</para>
    ///
    /// <para>Decoded by hand rather than with a JWT library: the token has just been issued by the
    /// Identity process under test and is about to be sent straight back to it, so nothing here
    /// needs to validate a signature - and adding a JWT package to this project to read one claim
    /// would be the only reason it was there.</para>
    /// </summary>
    public static string UserIdFromAccessToken(string accessToken)
    {
        var segments = accessToken.Split('.');
        Assert.That(segments, Has.Length.GreaterThanOrEqualTo(2), "access token is not a JWT");

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return document.RootElement.GetProperty("sub").GetString()!;
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
