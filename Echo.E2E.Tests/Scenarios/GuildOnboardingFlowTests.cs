using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Walks the whole onboarding feature against a live stack: an owner configures a rules screen plus
/// a prompt that grants a role and unlocks a channel, a new member joins by invite and is
/// participation-restricted until they accept, accepting applies the grants, and Channels &amp;
/// Roles takes them back on deselect.
/// </summary>
[TestFixture]
[Category("E2E")]
public class GuildOnboardingFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "onboarding", "onboarding-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    private static HttpClient AuthedClient(SpawnedServiceProcess service, string token)
    {
        var client = new HttpClient { BaseAddress = service.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string what)
    {
        Assert.That(response.IsSuccessStatusCode, Is.True,
            $"{what} failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Test]
    public async Task PromptOnboarding_GatesJoin_ThenGrantsAndRevokesOnDeselect()
    {
        var (_, ownerToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "onbowner");
        using var owner = AuthedClient(_stack.Guild, ownerToken);

        var guild = await ReadJsonAsync(
            await owner.PostAsJsonAsync("/api/v1/guilds", new { Name = "Onboarded Guild" }), "Create guild");
        var guildId = guild.GetProperty("id").GetString()!;

        // A role for the prompt to hand out, and a channel for it to unlock.
        var role = await ReadJsonAsync(
            await owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/roles", new { Name = "Gamer", Color = "#00ff00" }),
            "Create role");
        var roleId = role.GetProperty("id").GetString()!;

        var channel = await ReadJsonAsync(
            await owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/channels",
                new { Name = "gaming", Description = "games", Type = "Text" }),
            "Create channel");
        var channelId = channel.GetProperty("id").GetString()!;

        // --- Configure onboarding: rules text plus one optional prompt. ---

        var config = await ReadJsonAsync(await owner.PutAsJsonAsync($"/api/v1/guilds/{guildId}/onboarding", new
        {
            Enabled = true,
            RulesText = "1. Be nice",
            Prompts = new[]
            {
                new
                {
                    Title = "What brings you here?",
                    Type = "MultipleChoice",
                    SingleSelect = false,
                    Required = false,
                    InOnboarding = true,
                    Options = new[]
                    {
                        new { Title = "Gaming", RoleIds = new[] { roleId }, ChannelIds = new[] { channelId } },
                    },
                },
            },
        }), "Configure onboarding");

        var prompt = config.GetProperty("prompts")[0];
        var promptId = prompt.GetProperty("id").GetString()!;
        var optionId = prompt.GetProperty("options")[0].GetProperty("id").GetString()!;

        // --- A new member joins by invite while onboarding is on. ---

        var invite = await ReadJsonAsync(
            await owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/invite", new { Type = "Permanent" }), "Create invite");
        var inviteId = invite.GetProperty("id").GetString()!;

        var (_, joinerToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "onbjoiner");
        using var joiner = AuthedClient(_stack.Guild, joinerToken);

        // Redeem returns 200 with no body, so this one is a status assertion rather than ReadJsonAsync.
        var redeemResponse = await joiner.PostAsync($"/api/v1/invites/{inviteId}/redeem", null);
        await E2EAssert.SucceededAsync(redeemResponse, _stack.Guild, "Redeem invite failed");

        var statusBefore = await ReadJsonAsync(
            await joiner.GetAsync($"/api/v1/guilds/{guildId}/onboarding/me"), "Read onboarding status");

        Assert.Multiple(() =>
        {
            Assert.That(statusBefore.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(statusBefore.GetProperty("completed").GetBoolean(), Is.False);
            Assert.That(statusBefore.GetProperty("rulesText").GetString(), Is.EqualTo("1. Be nice"));
            Assert.That(statusBefore.GetProperty("prompts").GetArrayLength(), Is.EqualTo(1));
        });

        // Moderators can see who is stuck on the rules screen.
        var pending = await ReadJsonAsync(
            await owner.GetAsync($"/api/v1/guilds/{guildId}/members/pending"), "List pending members");
        Assert.That(pending.GetArrayLength(), Is.EqualTo(1), "the joiner should be reported as pending");

        // --- Accept with an answer: grants land and the gate lifts. ---

        var acceptResponse = await joiner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/onboarding/accept", new
        {
            Responses = new[] { new { PromptId = promptId, OptionIds = new[] { optionId } } },
        });
        await E2EAssert.SucceededAsync(acceptResponse, _stack.Guild, "Accept failed");

        var statusAfter = await ReadJsonAsync(
            await joiner.GetAsync($"/api/v1/guilds/{guildId}/onboarding/me"), "Re-read onboarding status");
        Assert.That(statusAfter.GetProperty("completed").GetBoolean(), Is.True);

        var promptsView = await ReadJsonAsync(
            await joiner.GetAsync($"/api/v1/guilds/{guildId}/onboarding/prompts"), "Read Channels & Roles");
        Assert.That(promptsView[0].GetProperty("options")[0].GetProperty("selected").GetBoolean(), Is.True);

        var pendingAfter = await ReadJsonAsync(
            await owner.GetAsync($"/api/v1/guilds/{guildId}/members/pending"), "Re-list pending members");
        Assert.That(pendingAfter.GetArrayLength(), Is.Zero, "accepting must clear the member from the pending report");

        // --- Deselect in Channels & Roles: the grant is taken back. ---

        var deselectResponse = await joiner.PutAsJsonAsync($"/api/v1/guilds/{guildId}/onboarding/me/responses", new
        {
            Responses = new[] { new { PromptId = promptId, OptionIds = Array.Empty<string>() } },
        });
        await E2EAssert.SucceededAsync(deselectResponse, _stack.Guild, "Updating responses failed");

        var promptsAfterDeselect = await ReadJsonAsync(
            await joiner.GetAsync($"/api/v1/guilds/{guildId}/onboarding/prompts"), "Re-read Channels & Roles");
        Assert.That(promptsAfterDeselect[0].GetProperty("options")[0].GetProperty("selected").GetBoolean(), Is.False);
    }

    [Test]
    public async Task PrivilegedRoleInPromptOption_IsRejected()
    {
        var (_, ownerToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "onbsecowner");
        using var owner = AuthedClient(_stack.Guild, ownerToken);

        var guild = await ReadJsonAsync(
            await owner.PostAsJsonAsync("/api/v1/guilds", new { Name = "Escalation Guild" }), "Create guild");
        var guildId = guild.GetProperty("id").GetString()!;

        var role = await ReadJsonAsync(
            await owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/roles",
                new { Name = "Mod", Color = "#ff0000", Permissions = "BanMembers" }),
            "Create moderator role");
        var roleId = role.GetProperty("id").GetString()!;

        var response = await owner.PutAsJsonAsync($"/api/v1/guilds/{guildId}/onboarding", new
        {
            Enabled = true,
            RulesText = "Rules",
            Prompts = new[]
            {
                new
                {
                    Title = "Want to moderate?",
                    Type = "MultipleChoice",
                    Options = new[] { new { Title = "Yes please", RoleIds = new[] { roleId } } },
                },
            },
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "onboarding must never be able to hand out a role carrying moderation permissions: " +
            await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task WelcomeScreen_IsVisibleOnTheInvitePreviewBeforeJoining()
    {
        var (_, ownerToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "welcomeowner");
        using var owner = AuthedClient(_stack.Guild, ownerToken);

        var guild = await ReadJsonAsync(
            await owner.PostAsJsonAsync("/api/v1/guilds", new { Name = "Welcoming Guild" }), "Create guild");
        var guildId = guild.GetProperty("id").GetString()!;

        var channel = await ReadJsonAsync(
            await owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/channels",
                new { Name = "start-here", Description = "", Type = "Text" }),
            "Create channel");
        var channelId = channel.GetProperty("id").GetString()!;

        await ReadJsonAsync(await owner.PutAsJsonAsync($"/api/v1/guilds/{guildId}/welcome-screen", new
        {
            Enabled = true,
            Description = "A friendly place",
            Channels = new[] { new { ChannelId = channelId, Description = "read this first", Emoji = "👋" } },
        }), "Configure welcome screen");

        var invite = await ReadJsonAsync(
            await owner.PostAsJsonAsync($"/api/v1/guilds/{guildId}/invite", new { Type = "Permanent" }), "Create invite");
        var code = invite.GetProperty("code").GetString()!;

        // Deliberately unauthenticated-as-a-member: the preview is what a not-yet-member sees.
        var (_, outsiderToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "welcomeoutsider");
        using var outsider = AuthedClient(_stack.Guild, outsiderToken);

        var preview = await ReadJsonAsync(
            await outsider.GetAsync($"/api/v1/invites/code/{code}"), "Read invite preview");

        var welcomeScreen = preview.GetProperty("welcomeScreen");
        Assert.Multiple(() =>
        {
            Assert.That(welcomeScreen.GetProperty("description").GetString(), Is.EqualTo("A friendly place"));
            Assert.That(welcomeScreen.GetProperty("channels").GetArrayLength(), Is.EqualTo(1));
        });
    }
}
