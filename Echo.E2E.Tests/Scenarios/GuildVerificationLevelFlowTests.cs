using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Npgsql;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves guild verification levels gate invite redemption end to end, exercising the real
/// cross-service call from Guild into Identity (GetUserByIdRequest/Response over the bus) that
/// InviteEndpoint.RedeemInviteAsync uses to check the joining account's email-confirmation status
/// and age. This harness registers users with AUTH_REQUIRE_USER_EMAIL_VERIFICATION=false (see
/// EchoTestStack.Common), so every account is auto-EmailConfirmed=true at creation - the only
/// variable this test can actually flip is account age, so it targets the "Medium" tier (verified
/// email + 5 minutes old) and backdates the joining user's real asp_net_users.created_at row
/// directly in Postgres (same "assert/arrange against real infra directly" pattern
/// AccountDeletionFlowTests uses) to simulate an account old enough to pass, rather than waiting
/// 5 real minutes.
/// </summary>
[TestFixture]
[Category("E2E")]
public class GuildVerificationLevelFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "verifylvl", "verifylvl-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    private static async Task<(string userId, string token)> RegisterAndGetTokenAsync(
        SpawnedServiceProcess identity, string username)
    {
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

        return (userId, tokenBody.GetProperty("access_token").GetString()!);
    }

    private static HttpClient AuthedClient(SpawnedServiceProcess service, string token)
    {
        var client = new HttpClient { BaseAddress = service.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task BackdateAccountCreationAsync(string userId, TimeSpan age)
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = EchoInfraFixture.Default.PostgresHost,
            Port = EchoInfraFixture.Default.PostgresPort,
            Database = "identity_verifylvl",
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE asp_net_users SET created_at = @createdAt WHERE id = @id", connection);
        command.Parameters.AddWithValue("createdAt", DateTimeOffset.UtcNow - age);
        command.Parameters.AddWithValue("id", userId);
        var rows = await command.ExecuteNonQueryAsync();
        Assert.That(rows, Is.EqualTo(1), $"Expected to backdate exactly one asp_net_users row for {userId}.");
    }

    [Test]
    public async Task MediumVerificationLevel_RejectsFreshAccount_ThenAllowsAgedAccount()
    {
        var (_, ownerToken) = await RegisterAndGetTokenAsync(_stack.Identity, "verifyowner");
        using var guildOwner = AuthedClient(_stack.Guild, ownerToken);

        var createGuildResponse = await guildOwner.PostAsJsonAsync("/api/v1/guilds", new { Name = "Verification Gated Guild" });
        Assert.That(createGuildResponse.IsSuccessStatusCode, Is.True,
            $"Create guild failed: {await createGuildResponse.Content.ReadAsStringAsync()}");
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;
        var guildName = createdGuild.GetProperty("name").GetString()!;

        // --- Set the guild to Medium verification (verified email + 5 minute old account). ---

        var updateResponse = await guildOwner.PatchAsJsonAsync($"/api/v1/guilds/{guildId}", new
        {
            Name = guildName,
            VerificationLevel = "Medium",
        });
        Assert.That(updateResponse.IsSuccessStatusCode, Is.True,
            $"Update guild verification level failed: {await updateResponse.Content.ReadAsStringAsync()}\n{_stack.Guild.CapturedOutput}");
        var updatedGuild = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(updatedGuild.GetProperty("verificationLevel").GetString(), Is.EqualTo("Medium"));

        // Permanent, multi-use invite - the endpoint increments UseCount before the verification
        // check runs, so a rejected attempt must not exhaust a OneTime invite before the real join.
        var createInviteResponse = await guildOwner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/invite", new { Type = "Permanent" });
        Assert.That(createInviteResponse.IsSuccessStatusCode, Is.True,
            $"Create invite failed: {await createInviteResponse.Content.ReadAsStringAsync()}");
        var invite = await createInviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = invite.GetProperty("id").GetString()!;

        // --- Act: a brand-new account (age ~0) tries to redeem - must be rejected. ---

        var (newUserId, newUserToken) = await RegisterAndGetTokenAsync(_stack.Identity, "freshjoiner");
        using var newUserGuild = AuthedClient(_stack.Guild, newUserToken);

        var rejectedResponse = await newUserGuild.PostAsync($"/api/v1/invites/{inviteId}/redeem", null);
        Assert.That(rejectedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            $"A fresh account should not meet the Medium verification bar: {await rejectedResponse.Content.ReadAsStringAsync()}\n{_stack.Guild.CapturedOutput}");
        var rejectedBody = await rejectedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(rejectedBody.GetProperty("error").GetString(), Is.EqualTo("verification_level_not_met"));
            Assert.That(rejectedBody.GetProperty("requiredLevel").GetString(), Is.EqualTo("Medium"));
        });

        // --- Act: backdate the same account past the 5-minute bar, then retry. ---

        await BackdateAccountCreationAsync(newUserId, TimeSpan.FromMinutes(10));

        var acceptedResponse = await newUserGuild.PostAsync($"/api/v1/invites/{inviteId}/redeem", null);
        Assert.That(acceptedResponse.IsSuccessStatusCode, Is.True,
            $"An account old enough to meet the Medium bar should be allowed to join: {await acceptedResponse.Content.ReadAsStringAsync()}\n{_stack.Guild.CapturedOutput}");
    }

    [Test]
    public async Task NoneVerificationLevel_AllowsFreshAccountToJoin()
    {
        var (_, ownerToken) = await RegisterAndGetTokenAsync(_stack.Identity, "noverifyowner");
        using var guildOwner = AuthedClient(_stack.Guild, ownerToken);

        var createGuildResponse = await guildOwner.PostAsJsonAsync("/api/v1/guilds", new { Name = "Ungated Guild" });
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;
        Assert.That(createdGuild.GetProperty("verificationLevel").GetString(), Is.EqualTo("None"),
            "Guilds must default to no verification requirement.");

        var createInviteResponse = await guildOwner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/invite", new { Type = "OneTime" });
        var invite = await createInviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = invite.GetProperty("id").GetString()!;

        var (_, freshToken) = await RegisterAndGetTokenAsync(_stack.Identity, "freshjoinerok");
        using var freshGuild = AuthedClient(_stack.Guild, freshToken);

        var redeemResponse = await freshGuild.PostAsync($"/api/v1/invites/{inviteId}/redeem", null);
        Assert.That(redeemResponse.IsSuccessStatusCode, Is.True,
            $"A fresh account must be able to join a guild with no verification requirement: {await redeemResponse.Content.ReadAsStringAsync()}");
    }
}
