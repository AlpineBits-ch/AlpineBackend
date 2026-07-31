using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;
using Npgsql;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves thread creation and reading works end to end through the real Guild + Messaging
/// services: creating a thread under a guild's default text channel, listing threads for that
/// channel, and - since ThreadEndpoint.CreateThreadAsync now optionally posts an initial message
/// via a real cross-service CreateMessageCommand call into Messaging - confirming that message
/// actually lands and is readable back out of Messaging's own history endpoint, not just that
/// Guild's thread-creation response looked right in isolation.
/// </summary>
[TestFixture]
[Category("E2E")]
public class ThreadFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "threads", "threads-test-instance");
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
    public async Task CreateThread_WithInitialContent_IsListedAndMessageIsReadable()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "threaduser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        // --- Arrange: a guild with its default "general" text channel. ---

        var createGuildResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "Thread Test Guild" });
        await E2EAssert.SucceededAsync(createGuildResponse, _stack.Guild, "Create guild failed");
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;

        var channelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildId}/channels");
        Assert.That(channelsResponse.IsSuccessStatusCode, Is.True,
            $"List channels failed: {await channelsResponse.Content.ReadAsStringAsync()}");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var textChannelId = channels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;

        // --- Act: create a thread under it, with an initial message. ---

        var createThreadResponse = await guild.PostAsJsonAsync($"/api/v1/channels/{textChannelId}/threads", new
        {
            Name = "my first thread",
            Content = "hello from inside the thread",
        });
        await E2EAssert.SucceededAsync(createThreadResponse, _stack.Guild, "Create thread failed");
        var thread = await createThreadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var threadId = thread.GetProperty("id").GetString()!;

        Assert.Multiple(() =>
        {
            Assert.That(thread.GetProperty("type").GetString(), Is.EqualTo("Thread"));
            Assert.That(thread.GetProperty("parentChannelId").GetString(), Is.EqualTo(textChannelId));
            Assert.That(thread.GetProperty("name").GetString(), Is.EqualTo("my first thread"));
        });

        // --- Assert: reading the thread back - both "is it listed" and "is its message there". ---

        var listThreadsResponse = await guild.GetAsync($"/api/v1/channels/{textChannelId}/threads");
        await E2EAssert.SucceededAsync(listThreadsResponse, _stack.Guild, "List threads failed");
        var threads = await listThreadsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var listedThreadIds = threads.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.That(listedThreadIds, Does.Contain(threadId),
            $"Newly created thread {threadId} was not present in GET .../threads.");

        // The initial message is created via a real cross-service CreateMessageCommand call from
        // Guild into Messaging - poll rather than assume it's landed by the time we ask.
        JsonElement? foundMessage = null;
        string? lastFailureBody = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!cts.IsCancellationRequested && foundMessage is null)
        {
            var messagesResponse = await messaging.GetAsync($"/api/v1/messaging/channels/{threadId}/messages?offset=0&limit=20");
            if (messagesResponse.IsSuccessStatusCode)
            {
                var messages = await messagesResponse.Content.ReadFromJsonAsync<JsonElement>();
                // Content is byte[] on the wire (base64), not plaintext - this is the same
                // "plaintext fallback stored as raw UTF8 bytes" convention AccountDeletionFlowTests
                // decodes when reading Message.Content directly from Postgres.
                foundMessage = messages.EnumerateArray()
                    .Cast<JsonElement?>()
                    .FirstOrDefault(m => System.Text.Encoding.UTF8.GetString(m!.Value.GetProperty("content").GetBytesFromBase64())
                        == "hello from inside the thread");
            }
            else
            {
                lastFailureBody = $"status={messagesResponse.StatusCode} body={await messagesResponse.Content.ReadAsStringAsync()}";
            }

            if (foundMessage is null)
                await Task.Delay(500, CancellationToken.None);
        }

        if (foundMessage is null)
        {
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = EchoInfraFixture.Default.PostgresHost,
                Port = EchoInfraFixture.Default.PostgresPort,
                Database = "messaging_threads",
                Username = "postgres",
                Password = "postgres",
            }.ConnectionString;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT channel_id, context_id, content FROM messages", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
                rows.Add($"channel_id={reader.GetString(0)} context_id={reader.GetString(1)} content={System.Text.Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(2))}");

            Assert.Fail(
                $"The thread's initial message never became readable via Messaging's own history endpoint within 30s (threadId={threadId}). " +
                $"Last HTTP failure: {lastFailureBody}. Raw messages table contents:\n{string.Join("\n", rows)}\n{_stack.Messaging.CapturedOutput}");
        }
    }

    [Test]
    public async Task CreateThread_WithoutContent_OpensEmptyButIsStillListed()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "threaduser2");
        using var guild = AuthedClient(_stack.Guild, token);

        var createGuildResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = "Empty Thread Guild" });
        Assert.That(createGuildResponse.IsSuccessStatusCode, Is.True);
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;

        var channelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildId}/channels");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var textChannelId = channels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;

        // No Content field at all - must not throw, and must not silently require it.
        var createThreadResponse = await guild.PostAsJsonAsync($"/api/v1/channels/{textChannelId}/threads", new
        {
            Name = "empty thread",
        });
        await E2EAssert.SucceededAsync(createThreadResponse, _stack.Guild, "Create thread without content failed");
        var thread = await createThreadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var threadId = thread.GetProperty("id").GetString()!;

        var listThreadsResponse = await guild.GetAsync($"/api/v1/channels/{textChannelId}/threads");
        await E2EAssert.SucceededAsync(listThreadsResponse, _stack.Guild, "List threads (no-content case) failed");
        // Read after the assertion, not before: this one is genuinely used, but reading it up front
        // only to interpolate it into a message that usually never renders is the pattern being
        // removed here.
        var listThreadsRaw = await listThreadsResponse.Content.ReadAsStringAsync();
        var threads = JsonDocument.Parse(listThreadsRaw).RootElement;
        var listedThreadIds = threads.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.That(listedThreadIds, Does.Contain(threadId));
    }
}
