using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;
using Npgsql;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves custom guild emoji work end to end as message reactions - specifically the real
/// cross-service validation call from Messaging into Guild (GetGuildEmojiRequest/Response over
/// the bus, see GetGuildEmojiHandler) that resolves an emojiId to its name and confirms it
/// actually belongs to the guild that owns the reacted-to channel.
///
/// This harness has no S3/Minio testcontainer (see EchoInfraSet - only Postgres/RabbitMQ/Redis
/// are provisioned), and GuildEmojiController.CreateEmoji requires a real multipart image upload
/// through IAmazonS3, so a real upload isn't exercisable here. Per this test suite's guidance for
/// that constraint, the emoji row is seeded directly into Guild's own Postgres database (same
/// "arrange against real infra directly" pattern AccountDeletionFlowTests uses for its polling
/// assertions) - this still exercises the real thing under test (cross-service emoji-by-id
/// resolution and reaction storage/read-back), just without a real image byte stream neither
/// side of that interaction actually inspects.
/// </summary>
[TestFixture]
[Category("E2E")]
public class CustomEmojiReactionFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "emoji", "emoji-test-instance");
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

    /// <summary>Seeds a GuildEmoji row directly (columns per Guild.Infrastructure's AddGuildEmoji
    /// migration: id, guild_id, name, created_by_user_id, animated, created_at, updated_at) -
    /// standing in for a real multipart upload this harness can't exercise (no S3/Minio).</summary>
    private static async Task<string> SeedGuildEmojiAsync(string guildId, string userId, string name)
    {
        var emojiId = $"emoj_test{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = EchoInfraFixture.Default.PostgresHost,
            Port = EchoInfraFixture.Default.PostgresPort,
            Database = "guild_emoji",
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO guild_emojis (id, guild_id, name, created_by_user_id, animated, created_at, updated_at)
            VALUES (@id, @guildId, @name, @userId, false, @now, @now)
            """, connection);
        command.Parameters.AddWithValue("id", emojiId);
        command.Parameters.AddWithValue("guildId", guildId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();

        return emojiId;
    }

    [Test]
    public async Task ReactWithCustomEmoji_ResolvesNameFromGuild_AndIsReadableBack()
    {
        var (userId, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "emojiuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var createGuildResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "Emoji Test Guild" });
        Assert.That(createGuildResponse.IsSuccessStatusCode, Is.True,
            $"Create guild failed: {await createGuildResponse.Content.ReadAsStringAsync()}");
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;

        var channelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildId}/channels");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var textChannelId = channels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;

        var emojiId = await SeedGuildEmojiAsync(guildId, userId, "pepega");

        var sendResponse = await messaging.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = "react to me",
            ChannelId = textChannelId,
        });
        Assert.That(sendResponse.IsSuccessStatusCode, Is.True,
            $"Send message failed: {await sendResponse.Content.ReadAsStringAsync()}");
        var message = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;

        // --- Act: react with the custom emoji by id (no unicode `reaction` field). ---

        var reactResponse = await messaging.PostAsJsonAsync($"/api/v1/messages/{messageId}/reactions", new
        {
            ChannelId = textChannelId,
            EmojiId = emojiId,
        });
        Assert.That(reactResponse.IsSuccessStatusCode, Is.True,
            $"React with custom emoji failed: {await reactResponse.Content.ReadAsStringAsync()}\n{_stack.Messaging.CapturedOutput}");

        // --- Assert: reading the message back shows the resolved name + the emoji id. ---

        JsonElement? reaction = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cts.IsCancellationRequested && reaction is null)
        {
            var historyResponse = await messaging.GetAsync($"/api/v1/messaging/channels/{textChannelId}/messages?offset=0&limit=20");
            if (historyResponse.IsSuccessStatusCode)
            {
                var history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
                var found = history.EnumerateArray().FirstOrDefault(m => m.GetProperty("id").GetString() == messageId);
                if (found.ValueKind != JsonValueKind.Undefined)
                {
                    var reactions = found.GetProperty("reactions").EnumerateArray().ToList();
                    if (reactions.Count > 0) reaction = reactions[0];
                }
            }
            if (reaction is null) await Task.Delay(300);
        }

        Assert.That(reaction, Is.Not.Null, "The custom emoji reaction never became readable via message history.");
        Assert.Multiple(() =>
        {
            Assert.That(reaction!.Value.GetProperty("emojiId").GetString(), Is.EqualTo(emojiId));
            Assert.That(reaction!.Value.GetProperty("emoji").GetString(), Is.EqualTo("pepega"),
                "The reaction's emoji name must be resolved server-side from the guild's emoji, not trusted from the client.");
            Assert.That(reaction!.Value.GetProperty("userId").GetString(), Is.EqualTo(userId));
        });
    }

    [Test]
    public async Task ReactWithUnknownEmojiId_Returns404()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "emojiuser2");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var createGuildResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "No Emoji Guild" });
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;

        var channelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildId}/channels");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var textChannelId = channels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;

        var sendResponse = await messaging.PostAsJsonAsync("/api/v1/messaging", new { Content = "hi", ChannelId = textChannelId });
        var message = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;

        var reactResponse = await messaging.PostAsJsonAsync($"/api/v1/messages/{messageId}/reactions", new
        {
            ChannelId = textChannelId,
            EmojiId = "emoj_doesnotexist",
        });
        Assert.That(reactResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "Reacting with an emoji id that doesn't belong to the guild must 404.");
    }

    [Test]
    public async Task ReactWithCustomEmoji_WithoutChannelId_IsRejected()
    {
        var (userIdA, tokenA) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "emojidm_a");
        var (userIdB, tokenB) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "emojidm_b");
        using var messagingA = AuthedClient(_stack.Messaging, tokenA);
        using var socialA = AuthedClient(_stack.Social, tokenA);
        using var socialB = AuthedClient(_stack.Social, tokenB);

        // A DM conversation requires the members to already be friends (see
        // AccountDeletionFlowTests for the same friend-request -> accept dance).
        var bProfileResponse = await socialA.GetAsync($"/api/v1/profiles/by-user/{userIdB}");
        var bProfile = await bProfileResponse.Content.ReadFromJsonAsync<JsonElement>();
        var bUserName = bProfile.GetProperty("userName").GetString();
        await socialA.PostAsJsonAsync("/api/v1/relationships", new { UserName = bUserName, Hash = 0 });
        var bRelationshipsResponse = await socialB.GetAsync("/api/v1/relationships");
        var bRelationships = await bRelationshipsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pendingIncomingId = bRelationships.EnumerateArray()
            .First(r => r.GetProperty("status").GetString() == "PendingIncoming")
            .GetProperty("id").GetString()!;
        await socialB.PostAsync($"/api/v1/relationships/{pendingIncomingId}/accept", null);

        var createConversationResponse = await messagingA.PostAsJsonAsync("/api/v1/conversations", new
        {
            Encryption = "Plain",
            Members = new[] { new { UserId = userIdB } },
        });
        var conversation = await createConversationResponse.Content.ReadFromJsonAsync<JsonElement>();
        var conversationId = conversation.GetProperty("id").GetString()!;

        var sendResponse = await messagingA.PostAsJsonAsync("/api/v1/messaging", new { Content = "hi", ConversationId = conversationId });
        var message = await sendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;

        // Custom emoji reactions require a ChannelId (guild channels only) - a DM has none.
        var reactResponse = await messagingA.PostAsJsonAsync($"/api/v1/messages/{messageId}/reactions", new
        {
            ConversationId = conversationId,
            EmojiId = "emoj_whatever",
        });
        Assert.That(reactResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "Custom emoji reactions must be rejected outside guild channels (no ChannelId).");
    }
}
