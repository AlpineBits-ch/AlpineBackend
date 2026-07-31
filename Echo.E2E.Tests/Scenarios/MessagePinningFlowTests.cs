using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves message pinning end to end through the real Guild + Messaging services: a guild
/// channel message can be pinned (checking the real cross-service PinMessages permission check
/// that round-trips through Guild's HasUserPermissionToChannelHandler), shows up in the
/// pinned-messages list, and disappears again after unpinning. Also covers the DM/conversation
/// path, which is gated by ConversationPermissionService instead of a guild permission - a
/// different code path than the channel case, worth exercising in the same run since both are
/// new in this feature.
/// </summary>
[TestFixture]
[Category("E2E")]
public class MessagePinningFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "pinning", "pinning-test-instance");
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
    public async Task PinAndUnpinChannelMessage_UpdatesPinnedList()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "pinuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var createGuildResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "Pin Test Guild" });
        Assert.That(createGuildResponse.IsSuccessStatusCode, Is.True,
            $"Create guild failed: {await createGuildResponse.Content.ReadAsStringAsync()}");
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;

        var channelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildId}/channels");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var textChannelId = channels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;

        var sendResponse = await messaging.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = "pin me",
            ChannelId = textChannelId,
        });
        await E2EAssert.SucceededAsync(sendResponse, _stack.Messaging, "Send message failed");
        var message = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;
        Assert.That(message.GetProperty("isPinned").GetBoolean(), Is.False,
            "A freshly sent message must not start out pinned.");

        // --- Act: pin it. ---

        var pinResponse = await messaging.PostAsync($"/api/v1/messaging/{messageId}/pin", null);
        await E2EAssert.SucceededAsync(pinResponse, _stack.Messaging, "Pin failed");
        var pinBody = await pinResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(pinBody.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(pinBody.GetProperty("channelId").GetString(), Is.EqualTo(textChannelId));
            Assert.That(pinBody.TryGetProperty("pinnedById", out _), Is.True);
        });

        // Idempotency: pinning an already-pinned message just returns current state, not an error.
        var pinAgainResponse = await messaging.PostAsync($"/api/v1/messaging/{messageId}/pin", null);
        Assert.That(pinAgainResponse.IsSuccessStatusCode, Is.True,
            $"Re-pinning an already-pinned message should be idempotent: {await pinAgainResponse.Content.ReadAsStringAsync()}");

        // --- Assert: shows up in the pinned list. ---

        var pinnedListResponse = await messaging.GetAsync($"/api/v1/messaging/pins?channelId={textChannelId}");
        Assert.That(pinnedListResponse.IsSuccessStatusCode, Is.True,
            $"List pinned messages failed: {await pinnedListResponse.Content.ReadAsStringAsync()}");
        var pinnedList = await pinnedListResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pinnedIds = pinnedList.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.That(pinnedIds, Does.Contain(messageId));

        // Message history should also reflect isPinned=true on the message itself.
        var historyResponse = await messaging.GetAsync($"/api/v1/messaging/channels/{textChannelId}/messages?offset=0&limit=20");
        var history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pinnedMessage = history.EnumerateArray().First(m => m.GetProperty("id").GetString() == messageId);
        Assert.That(pinnedMessage.GetProperty("isPinned").GetBoolean(), Is.True);

        // --- Act: unpin it. ---

        var unpinResponse = await messaging.DeleteAsync($"/api/v1/messaging/{messageId}/pin");
        await E2EAssert.SucceededAsync(unpinResponse, _stack.Messaging, "Unpin failed");

        // --- Assert: gone from the pinned list. ---

        var pinnedListAfterResponse = await messaging.GetAsync($"/api/v1/messaging/pins?channelId={textChannelId}");
        var pinnedListAfter = await pinnedListAfterResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pinnedIdsAfter = pinnedListAfter.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.That(pinnedIdsAfter, Does.Not.Contain(messageId));
    }

    [Test]
    public async Task PinAndUnpinDirectMessage_UpdatesPinnedList()
    {
        var (userIdA, tokenA) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "dmpin_a");
        var (userIdB, tokenB) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "dmpin_b");
        using var messagingA = AuthedClient(_stack.Messaging, tokenA);
        using var socialA = AuthedClient(_stack.Social, tokenA);
        using var socialB = AuthedClient(_stack.Social, tokenB);

        // A DM conversation requires the members to already be friends (see
        // AccountDeletionFlowTests for the same friend-request -> accept dance).
        var bProfileResponse = await socialA.GetAsync($"/api/v1/profiles/by-user/{userIdB}");
        Assert.That(bProfileResponse.IsSuccessStatusCode, Is.True,
            $"Fetch B's profile failed: {await bProfileResponse.Content.ReadAsStringAsync()}");
        var bProfile = await bProfileResponse.Content.ReadFromJsonAsync<JsonElement>();
        var bUserName = bProfile.GetProperty("userName").GetString();

        var friendRequestResponse = await socialA.PostAsJsonAsync("/api/v1/relationships", new { UserName = bUserName, Hash = 0 });
        Assert.That(friendRequestResponse.IsSuccessStatusCode, Is.True,
            $"Friend request failed: {await friendRequestResponse.Content.ReadAsStringAsync()}");

        var bRelationshipsResponse = await socialB.GetAsync("/api/v1/relationships");
        var bRelationships = await bRelationshipsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pendingIncomingId = bRelationships.EnumerateArray()
            .First(r => r.GetProperty("status").GetString() == "PendingIncoming")
            .GetProperty("id").GetString()!;
        var acceptResponse = await socialB.PostAsync($"/api/v1/relationships/{pendingIncomingId}/accept", null);
        Assert.That(acceptResponse.IsSuccessStatusCode, Is.True,
            $"Accept friend request failed: {await acceptResponse.Content.ReadAsStringAsync()}");

        var createConversationResponse = await messagingA.PostAsJsonAsync("/api/v1/conversations", new
        {
            Encryption = "Plain",
            Members = new[] { new { UserId = userIdB } },
        });
        Assert.That(createConversationResponse.IsSuccessStatusCode, Is.True,
            $"Create conversation failed: {await createConversationResponse.Content.ReadAsStringAsync()}");
        var conversation = await createConversationResponse.Content.ReadFromJsonAsync<JsonElement>();
        var conversationId = conversation.GetProperty("id").GetString()!;

        var sendResponse = await messagingA.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = "pin this dm",
            ConversationId = conversationId,
        });
        Assert.That(sendResponse.IsSuccessStatusCode, Is.True,
            $"Send DM failed: {await sendResponse.Content.ReadAsStringAsync()}");
        var message = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;

        var pinResponse = await messagingA.PostAsync($"/api/v1/messaging/{messageId}/pin", null);
        await E2EAssert.SucceededAsync(pinResponse, _stack.Messaging, "Pin DM failed");
        var pinBody = await pinResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(pinBody.GetProperty("conversationId").GetString(), Is.EqualTo(conversationId));

        var pinnedListResponse = await messagingA.GetAsync($"/api/v1/messaging/pins?conversationId={conversationId}");
        Assert.That(pinnedListResponse.IsSuccessStatusCode, Is.True,
            $"List pinned DMs failed: {await pinnedListResponse.Content.ReadAsStringAsync()}");
        var pinnedList = await pinnedListResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(pinnedList.EnumerateArray().Select(m => m.GetProperty("id").GetString()), Does.Contain(messageId));

        var unpinResponse = await messagingA.DeleteAsync($"/api/v1/messaging/{messageId}/pin");
        Assert.That(unpinResponse.IsSuccessStatusCode, Is.True,
            $"Unpin DM failed: {await unpinResponse.Content.ReadAsStringAsync()}");

        var pinnedListAfterResponse = await messagingA.GetAsync($"/api/v1/messaging/pins?conversationId={conversationId}");
        var pinnedListAfter = await pinnedListAfterResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(pinnedListAfter.EnumerateArray().Select(m => m.GetProperty("id").GetString()), Does.Not.Contain(messageId));
    }
}
