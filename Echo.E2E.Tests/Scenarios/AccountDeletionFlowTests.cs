using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;
using Npgsql;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves the full "delete my account" flow end to end over real infrastructure: Identity's
/// grace-period sweep (AccountDeletionPurgeSweepService, sped up here via
/// ACCOUNT_DELETION_GRACE_PERIOD_SECONDS/ACCOUNT_DELETION_SWEEP_INTERVAL_SECONDS - see
/// EchoTestStack) fires a real AccountPurgeStartedEvent over RabbitMQ, Echo's AccountDeletionSaga
/// fans PurgeUserDataCommand out to Identity/Social/Guild/Messaging/ Federation/Import as real
/// separate processes, and each one's handler does its real Postgres write.
/// </summary>
[TestFixture]
[Category("E2E")]
public class AccountDeletionFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "acctdel", "acctdel-test-instance");
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

    private static NpgsqlConnectionStringBuilder ConnectionString(string database) => new()
    {
        Host = EchoInfraFixture.Default.PostgresHost,
        Port = EchoInfraFixture.Default.PostgresPort,
        Database = database,
        Username = "postgres",
        Password = "postgres",
    };

    /// <summary>Polls a scalar query every 500ms until it satisfies <paramref name="isDone"/>
    /// or <paramref name="timeout"/> elapses - the purge is asynchronous (real grace-period
    /// sweep + real cross-service bus fan-out), so there's no single instant to assert against.</summary>
    private static async Task<bool> PollUntilAsync(
        string connectionString, string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, bool> isDone,
        TimeSpan timeout)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            await using (var command = new NpgsqlCommand(sql, connection))
            {
                bind(command);
                await using var reader = await command.ExecuteReaderAsync();
                if (isDone(reader))
                    return true;
            }

            await Task.Delay(500, CancellationToken.None);
        }

        return false;
    }

    [Test]
    public async Task DeleteAccount_PurgesAcrossAllServices_WhilePreservingOtherUsersData()
    {
        var (userIdA, tokenA) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "acctdel_a");
        var (userIdB, tokenB) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "acctdel_b");

        using var identityA = AuthedClient(_stack.Identity, tokenA);
        using var guildA = AuthedClient(_stack.Guild, tokenA);
        using var guildB = AuthedClient(_stack.Guild, tokenB);
        using var socialA = AuthedClient(_stack.Social, tokenA);
        using var socialB = AuthedClient(_stack.Social, tokenB);
        using var messagingA = AuthedClient(_stack.Messaging, tokenA);

        // --- Arrange: A owns a guild that B joins, they become friends, A DMs B. ---

        var createGuildResponse = await guildA.PostAsJsonAsync("/api/v1/guilds", new { Name = "Deletion Test Guild" });
        Assert.That(createGuildResponse.IsSuccessStatusCode, Is.True,
            $"Create guild failed: {await createGuildResponse.Content.ReadAsStringAsync()}");
        var guild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = guild.GetProperty("id").GetString()!;
        Assert.That(guild.GetProperty("ownerId").GetString(), Is.EqualTo(userIdA));

        var createInviteResponse = await guildA.PostAsJsonAsync($"/api/v1/guilds/{guildId}/invite", new { Type = "OneTime" });
        Assert.That(createInviteResponse.IsSuccessStatusCode, Is.True,
            $"Create invite failed: {await createInviteResponse.Content.ReadAsStringAsync()}");
        var invite = await createInviteResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = invite.GetProperty("id").GetString()!;

        var redeemResponse = await guildB.PostAsync($"/api/v1/invites/{inviteId}/redeem", null);
        Assert.That(redeemResponse.IsSuccessStatusCode, Is.True,
            $"Redeem invite failed: {await redeemResponse.Content.ReadAsStringAsync()}");

        var bProfileResponse = await socialA.GetAsync($"/api/v1/profiles/by-user/{userIdB}");
        Assert.That(bProfileResponse.IsSuccessStatusCode, Is.True,
            $"Fetch B's profile failed: {await bProfileResponse.Content.ReadAsStringAsync()}");
        var bProfile = await bProfileResponse.Content.ReadFromJsonAsync<JsonElement>();
        var bUserName = bProfile.GetProperty("userName").GetString();

        // CreateFriendshipDto.Hash exists on the request shape but FriendshipEndpoints.CreateAsync
        // (Social.Application/Endpoints/FriendEndpoint.cs) matches purely on UserName - Hash is
        // unused, so any value satisfies the DTO's required int field.
        var friendRequestResponse = await socialA.PostAsJsonAsync("/api/v1/relationships", new { UserName = bUserName, Hash = 0 });
        Assert.That(friendRequestResponse.IsSuccessStatusCode, Is.True,
            $"Friend request failed: {await friendRequestResponse.Content.ReadAsStringAsync()}");

        var bRelationshipsResponse = await socialB.GetAsync("/api/v1/relationships");
        Assert.That(bRelationshipsResponse.IsSuccessStatusCode, Is.True,
            $"List relationships failed: {await bRelationshipsResponse.Content.ReadAsStringAsync()}");
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

        var sendMessageResponse = await messagingA.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = "hello before deletion",
            ConversationId = conversationId,
        });
        Assert.That(sendMessageResponse.IsSuccessStatusCode, Is.True,
            $"Send message failed: {await sendMessageResponse.Content.ReadAsStringAsync()}");
        var message = await sendMessageResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = message.GetProperty("id").GetString()!;
        Assert.That(message.GetProperty("authorId").GetString(), Is.EqualTo(userIdA));

        // --- Act: A deletes their account. ---

        var deleteResponse = await identityA.DeleteAsync("/api/v1/users/self");
        Assert.That(deleteResponse.IsSuccessStatusCode, Is.True,
            $"Delete request failed: {await deleteResponse.Content.ReadAsStringAsync()}");

        // --- Assert: real grace-period sweep + real cross-service purge fan-out land within a
        // generous margin over the 3s grace period + 2s sweep interval configured for this stack. ---

        var timeout = TimeSpan.FromSeconds(45);

        var identityTombstoned = await PollUntilAsync(
            ConnectionString("identity_acctdel").ConnectionString,
            "SELECT user_name, email, status::text FROM asp_net_users WHERE id = @id",
            cmd => cmd.Parameters.AddWithValue("id", userIdA),
            reader => reader.Read() && reader.GetString(0).StartsWith("Deleted User") && reader.IsDBNull(1) && reader.GetString(2) == "deleted",
            timeout);
        E2EAssert.Held(identityTombstoned, _stack.Identity, $"Identity tombstone (asp_net_users) never landed within {timeout}.");

        var socialAnonymized = await PollUntilAsync(
            ConnectionString("social_acctdel").ConnectionString,
            "SELECT user_name FROM profiles WHERE user_id = @id",
            cmd => cmd.Parameters.AddWithValue("id", userIdA),
            reader => reader.Read() && reader.GetString(0).StartsWith("Deleted User"),
            timeout);
        E2EAssert.Held(socialAnonymized, _stack.Social, $"Social profile anonymization never landed within {timeout}.");

        var relationshipsGone = await PollUntilAsync(
            ConnectionString("social_acctdel").ConnectionString,
            "SELECT COUNT(*) FROM relationships WHERE owner_id = @id OR target_id = @id",
            cmd => cmd.Parameters.AddWithValue("id", userIdA),
            reader => reader.Read() && reader.GetInt64(0) == 0,
            timeout);
        E2EAssert.Held(relationshipsGone, _stack.Social, $"Relationship rows for the deleted user were never removed within {timeout}.");

        var membershipGone = await PollUntilAsync(
            ConnectionString("guild_acctdel").ConnectionString,
            "SELECT COUNT(*) FROM guild_members WHERE guild_id = @gid AND user_id = @uid",
            cmd =>
            {
                cmd.Parameters.AddWithValue("gid", guildId);
                cmd.Parameters.AddWithValue("uid", userIdA);
            },
            reader => reader.Read() && reader.GetInt64(0) == 0,
            timeout);
        E2EAssert.Held(membershipGone, _stack.Guild, $"Guild membership for the deleted user was never removed within {timeout}.");

        var ownershipTransferred = await PollUntilAsync(
            ConnectionString("guild_acctdel").ConnectionString,
            "SELECT owner_id FROM guilds WHERE id = @gid",
            cmd => cmd.Parameters.AddWithValue("gid", guildId),
            reader => reader.Read() && reader.GetString(0) == userIdB,
            timeout);
        E2EAssert.Held(ownershipTransferred, _stack.Guild, $"Guild ownership was never transferred to the remaining member within {timeout}.");

        var conversationMembershipGone = await PollUntilAsync(
            ConnectionString("messaging_acctdel").ConnectionString,
            "SELECT COUNT(*) FROM members WHERE conversation_id = @cid AND user_id = @uid",
            cmd =>
            {
                cmd.Parameters.AddWithValue("cid", conversationId);
                cmd.Parameters.AddWithValue("uid", userIdA);
            },
            reader => reader.Read() && reader.GetInt64(0) == 0,
            timeout);
        E2EAssert.Held(conversationMembershipGone, _stack.Messaging, $"Conversation membership for the deleted user was never removed within {timeout}.");

        // Message content/authorship must survive untouched - this is the whole point of the
        // Discord-style tombstone design (see PurgeUserDataCommandHandler's doc comment in
        // Messaging.Application): B should still be able to read the conversation, with the
        // author now resolving (via Identity/Social) to "Deleted User", not a broken reference.
        await using (var messagingConnection = new NpgsqlConnection(ConnectionString("messaging_acctdel").ConnectionString))
        {
            await messagingConnection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT author_id, content FROM messages WHERE id = @id", messagingConnection);
            command.Parameters.AddWithValue("id", messageId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.That(await reader.ReadAsync(), Is.True, "The message sent before deletion no longer exists.");
            Assert.That(reader.GetString(0), Is.EqualTo(userIdA),
                "Message.AuthorId must be left pointing at the tombstoned user, not rewritten or nulled.");
            // Content is bytea (byte[]) - plaintext messages are stored as raw UTF8 bytes (see
            // Message.Content's doc comment: "plain-text fallback for any unmodified consumer").
            var contentBytes = reader.GetFieldValue<byte[]>(1);
            Assert.That(System.Text.Encoding.UTF8.GetString(contentBytes), Is.EqualTo("hello before deletion"),
                "Message content must survive account deletion untouched.");
        }
    }
}
