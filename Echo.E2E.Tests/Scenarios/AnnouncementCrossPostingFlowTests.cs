using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves Discord-style announcement channel cross-posting end to end across two guilds: an
/// Announcement channel in guild A gets followed by a channel in guild B (Guild-owned data,
/// GuildChannelFollow), and publishing a message in A's announcement channel copies it into B's
/// channel via a real cross-service call from Messaging (PublishEndpoint) into Guild
/// (GetChannelFollowersRequest/Response) and back into Messaging's own CreateMessageCommand for
/// the copy - not just a same-service echo.
/// </summary>
[TestFixture]
[Category("E2E")]
public class AnnouncementCrossPostingFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "crosspost", "crosspost-test-instance");
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

    [Test]
    public async Task PublishInAnnouncementChannel_CrossPostsToFollowingChannelInAnotherGuild()
    {
        // Same user owns both guilds - satisfies both sides of the follow permission check
        // (ViewChannel on the source, ManageChannel on the target guild) without needing a second
        // account, while still exercising the real two-guild, two-database fan-out.
        var (userId, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "crosspostowner");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var createGuildAResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "Announcement Source Guild" });
        Assert.That(createGuildAResponse.IsSuccessStatusCode, Is.True,
            $"Create guild A failed: {await createGuildAResponse.Content.ReadAsStringAsync()}");
        var guildA = await createGuildAResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildAId = guildA.GetProperty("id").GetString()!;

        var createGuildBResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "Follower Target Guild" });
        var guildB = await createGuildBResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildBId = guildB.GetProperty("id").GetString()!;

        var createAnnouncementChannelResponse = await guild.PostAsJsonAsync($"/api/v1/guilds/{guildAId}/channels", new
        {
            Name = "announcements",
            Type = "Announcement",
            Position = 1,
        });
        Assert.That(createAnnouncementChannelResponse.IsSuccessStatusCode, Is.True,
            $"Create announcement channel failed: {await createAnnouncementChannelResponse.Content.ReadAsStringAsync()}\n{_stack.Guild.CapturedOutput}");
        var announcementChannel = await createAnnouncementChannelResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sourceChannelId = announcementChannel.GetProperty("id").GetString()!;
        Assert.That(announcementChannel.GetProperty("type").GetString(), Is.EqualTo("Announcement"));

        var guildBChannelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildBId}/channels");
        var guildBChannels = await guildBChannelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var targetChannelId = guildBChannels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;

        // --- Act: follow the announcement channel from guild B's default text channel. ---

        var followResponse = await guild.PostAsJsonAsync($"/api/v1/channels/{sourceChannelId}/followers", new
        {
            TargetChannelId = targetChannelId,
        });
        Assert.That(followResponse.IsSuccessStatusCode, Is.True,
            $"Follow channel failed: {await followResponse.Content.ReadAsStringAsync()}\n{_stack.Guild.CapturedOutput}");
        var follow = await followResponse.Content.ReadFromJsonAsync<JsonElement>();
        var followId = follow.GetProperty("id").GetString()!;

        // Following the same pair twice must conflict, not create a duplicate subscription.
        var duplicateFollowResponse = await guild.PostAsJsonAsync($"/api/v1/channels/{sourceChannelId}/followers", new
        {
            TargetChannelId = targetChannelId,
        });
        Assert.That(duplicateFollowResponse.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Conflict));

        var listFollowersResponse = await guild.GetAsync($"/api/v1/channels/{sourceChannelId}/followers");
        Assert.That(listFollowersResponse.IsSuccessStatusCode, Is.True,
            $"List followers failed: {await listFollowersResponse.Content.ReadAsStringAsync()}");
        var followers = await listFollowersResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(followers.EnumerateArray().Select(f => f.GetProperty("targetChannelId").GetString()), Does.Contain(targetChannelId));

        // --- Act: post and publish a message in the announcement channel. ---

        var sendResponse = await messaging.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = "big news everyone",
            ChannelId = sourceChannelId,
        });
        Assert.That(sendResponse.IsSuccessStatusCode, Is.True,
            $"Send announcement message failed: {await sendResponse.Content.ReadAsStringAsync()}");
        var message = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;

        var publishResponse = await messaging.PostAsync($"/api/v1/messaging/{messageId}/publish", null);
        Assert.That(publishResponse.IsSuccessStatusCode, Is.True,
            $"Publish failed: {await publishResponse.Content.ReadAsStringAsync()}\n{_stack.Messaging.CapturedOutput}");
        var publishBody = await publishResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(publishBody.GetProperty("published").GetInt32(), Is.EqualTo(1));

        // --- Assert: the copy actually lands in guild B's following channel. ---

        JsonElement? crossPosted = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!cts.IsCancellationRequested && crossPosted is null)
        {
            var targetHistoryResponse = await messaging.GetAsync($"/api/v1/messaging/channels/{targetChannelId}/messages?offset=0&limit=20");
            if (targetHistoryResponse.IsSuccessStatusCode)
            {
                var targetHistory = await targetHistoryResponse.Content.ReadFromJsonAsync<JsonElement>();
                var found = targetHistory.EnumerateArray().Cast<JsonElement?>()
                    .FirstOrDefault(m => System.Text.Encoding.UTF8.GetString(m!.Value.GetProperty("content").GetBytesFromBase64()) == "big news everyone");
                if (found is not null) crossPosted = found;
            }
            if (crossPosted is null) await Task.Delay(300);
        }

        Assert.That(crossPosted, Is.Not.Null, "The published message never showed up in the following channel in guild B.");
        Assert.That(crossPosted!.Value.GetProperty("authorId").GetString(), Is.EqualTo(userId));
        // The original message in guild A must be untouched by publishing (still exactly one copy there).
        var sourceHistoryResponse = await messaging.GetAsync($"/api/v1/messaging/channels/{sourceChannelId}/messages?offset=0&limit=20");
        var sourceHistory = await sourceHistoryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(sourceHistory.EnumerateArray().Count(m =>
            System.Text.Encoding.UTF8.GetString(m.GetProperty("content").GetBytesFromBase64()) == "big news everyone"), Is.EqualTo(1));

        // --- Act: unfollow - a second publish must no longer reach guild B. ---

        var unfollowResponse = await guild.DeleteAsync($"/api/v1/channels/{sourceChannelId}/followers/{followId}");
        Assert.That(unfollowResponse.IsSuccessStatusCode, Is.True,
            $"Unfollow failed: {await unfollowResponse.Content.ReadAsStringAsync()}");

        var secondSendResponse = await messaging.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = "nobody should see this copy",
            ChannelId = sourceChannelId,
        });
        var secondMessage = await secondSendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondMessageId = secondMessage.GetProperty("id").GetString()!;

        var secondPublishResponse = await messaging.PostAsync($"/api/v1/messaging/{secondMessageId}/publish", null);
        Assert.That(secondPublishResponse.IsSuccessStatusCode, Is.True);
        var secondPublishBody = await secondPublishResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(secondPublishBody.GetProperty("published").GetInt32(), Is.EqualTo(0),
            "After unfollowing, publishing must report 0 recipients rather than still cross-posting.");
    }

    [Test]
    public async Task PublishInNonAnnouncementChannel_IsRejected()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "crosspostplain");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var createGuildResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "Plain Guild" });
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;

        var channelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildId}/channels");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var textChannelId = channels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;

        var sendResponse = await messaging.PostAsJsonAsync("/api/v1/messaging", new { Content = "not an announcement", ChannelId = textChannelId });
        var message = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;

        var publishResponse = await messaging.PostAsync($"/api/v1/messaging/{messageId}/publish", null);
        Assert.That(publishResponse.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest),
            "Publishing a message from a non-Announcement channel must be rejected.");
    }
}
