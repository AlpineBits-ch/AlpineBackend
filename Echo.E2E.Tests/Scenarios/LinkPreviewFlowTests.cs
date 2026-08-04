using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Link previews end to end (docs/specs/message-previews.md), against a real third-party origin.
/// </summary>
[TestFixture]
[Category("E2E")]
public class LinkPreviewFlowTests
{
    private EchoTestStack _stack = null!;
    private StubOriginServer _origin = null!;

    /// <summary>How long a preview is allowed to take to appear.</summary>
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(45);

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _origin = StubOriginServer.Start();
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "preview", "preview-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_stack is not null) await _stack.DisposeAsync();
        if (_origin is not null) await _origin.DisposeAsync();
    }

    private static HttpClient AuthedClient(SpawnedServiceProcess service, string token)
    {
        var client = new HttpClient { BaseAddress = service.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Creates a guild and returns its first text channel's id.</summary>
    private async Task<string> CreateChannelAsync(HttpClient guild, string name)
    {
        var createGuildResponse = await guild.PostAsJsonAsync("/api/v1/guilds", new { Name = name });
        await E2EAssert.SucceededAsync(createGuildResponse, _stack.Guild, "Create guild failed");
        var createdGuild = await createGuildResponse.Content.ReadFromJsonAsync<JsonElement>();
        var guildId = createdGuild.GetProperty("id").GetString()!;

        var channelsResponse = await guild.GetAsync($"/api/v1/guilds/{guildId}/channels");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        return channels.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "Text")
            .GetProperty("id").GetString()!;
    }

    private async Task<(string MessageId, JsonElement Body)> SendAsync(
        HttpClient messaging, string channelId, string content)
    {
        var response = await messaging.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = content,
            ChannelId = channelId,
        });
        await E2EAssert.SucceededAsync(response, _stack.Messaging, "Send message failed");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetString()!, body);
    }

    /// <summary>Reads one message back out of history.</summary>
    private static async Task<JsonElement> ReadMessageAsync(HttpClient messaging, string channelId, string messageId)
    {
        var response = await messaging.GetAsync($"/api/v1/messaging/channels/{channelId}/messages?offset=0&limit=50");
        var history = await response.Content.ReadFromJsonAsync<JsonElement>();
        return history.EnumerateArray().First(m => m.GetProperty("id").GetString() == messageId);
    }

    /// <summary>Polls history until the message carries at least one embed.</summary>
    private async Task<List<JsonElement>> WaitForEmbedsAsync(
        HttpClient messaging, string channelId, string messageId)
    {
        var deadline = DateTime.UtcNow + PreviewTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadMessageAsync(messaging, channelId, messageId);
            var embeds = ParseEmbeds(message);
            if (embeds.Count > 0) return embeds;

            await Task.Delay(500);
        }

        // Wolverine's own record, because the service logs cannot answer this: a cross-service
        // message that never arrives leaves the sender looking successful and the receiver looking
        // idle. See WolverineDiagnostics.
        var deadLetters = await WolverineDiagnostics.DeadLettersAsync(
            EchoInfraFixture.Default, _stack.MessagingDatabaseName);
        var incoming = await WolverineDiagnostics.IncomingSummaryAsync(
            EchoInfraFixture.Default, _stack.MessagingDatabaseName);

        Assert.Fail(
            $"No preview appeared within {PreviewTimeout.TotalSeconds:0}s.\n" +
            $"Origin saw: [{string.Join(", ", _origin.RequestedPaths)}]\n\n" +
            $"Messaging dead letters:\n" +
            (deadLetters.Count == 0
                ? "  (none)\n"
                : string.Join('\n', deadLetters.Select(d => $"  {d.MessageType} -> {d.ExceptionType}: {d.ExceptionMessage}")) + "\n") +
            $"\nMessaging incoming envelopes by type:\n{incoming}\n");
        return [];
    }

    private static List<JsonElement> ParseEmbeds(JsonElement message)
    {
        if (!message.TryGetProperty("embedsJson", out var embedsJson)) return [];
        if (embedsJson.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];

        var raw = embedsJson.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return [];

        using var parsed = JsonDocument.Parse(raw);
        return parsed.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>A message's body as text.</summary>
    private static string ContentOf(JsonElement message) =>
        System.Text.Encoding.UTF8.GetString(message.GetProperty("content").GetBytesFromBase64());

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ───────────────────────────────────────────────────────────────────────── The main flow
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PostingALink_AttachesAPreviewAfterTheMessageIsAlreadyDelivered()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "previewuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var channelId = await CreateChannelAsync(guild, "Preview Test Guild");

        // --- Act: send a message containing a link. ---

        var (messageId, sendBody) = await SendAsync(messaging, channelId, $"look at this {_origin.ArticleUrl}");

        // --- Assert: the send itself carries no preview. ---
        Assert.That(ParseEmbeds(sendBody), Is.Empty,
            "A message must be delivered before its links are resolved, never after.");

        // --- Assert: the preview arrives. ---

        var embeds = await WaitForEmbedsAsync(messaging, channelId, messageId);
        Assert.That(embeds, Has.Count.EqualTo(1));
        var embed = embeds[0];

        Assert.Multiple(() =>
        {
            Assert.That(StringOrNull(embed, "title"), Is.EqualTo(StubOriginServer.ArticleTitle),
                "og:title must win over the <title> element");
            Assert.That(StringOrNull(embed, "description"), Is.EqualTo(StubOriginServer.ArticleDescription));
            Assert.That(StringOrNull(embed, "url"), Is.EqualTo(_origin.ArticleUrl));
            Assert.That(StringOrNull(embed, "type"), Is.EqualTo("article"),
                "og:type article should pick the article layout");
            Assert.That(embed.GetProperty("provider").GetProperty("name").GetString(),
                Is.EqualTo(StubOriginServer.SiteName));
        });

        // --- Assert: the embed is marked as ours, not the author's. ---
        var flags = embed.GetProperty("flags").GetInt32();
        Assert.That(flags & (1 << 16), Is.Not.Zero, "generated embeds must carry the ServerGenerated flag");

        // --- Assert: the image was fetched, measured, re-hosted and is served back. ---

        var media = embed.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
            ? image
            : embed.GetProperty("thumbnail");

        Assert.Multiple(() =>
        {
            Assert.That(StringOrNull(media, "url"), Is.EqualTo($"{_origin.BaseUrl}/image.png"),
                "the origin's own URL is kept so 'open original' works");
            Assert.That(StringOrNull(media, "proxy_url"), Is.Not.Null.And.StartsWith(_stack.UnfurlBaseUrl),
                "clients must have a re-hosted copy to render, so the origin never sees a viewer's IP");
            // Measured by decoding, never taken from og:image:width - which the stub deliberately
            // never sends.
            Assert.That(media.GetProperty("width").GetInt32(), Is.EqualTo(StubOriginServer.ImageWidth));
            Assert.That(media.GetProperty("height").GetInt32(), Is.EqualTo(StubOriginServer.ImageHeight));
            Assert.That(StringOrNull(media, "placeholder"), Is.Not.Null.And.Not.Empty,
                "a blur placeholder keeps the card from reflowing when the image loads");
            Assert.That(media.GetProperty("placeholder_version").GetInt32(), Is.EqualTo(1));
        });

        // The proxy route is unauthenticated by design (an <img> tag carries no token) and must
        // really serve the bytes.
        using var anonymous = new HttpClient();
        var proxied = await anonymous.GetAsync(StringOrNull(media, "proxy_url"));
        Assert.Multiple(() =>
        {
            Assert.That(proxied.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(proxied.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
        });
        Assert.That((await proxied.Content.ReadAsByteArrayAsync()), Is.Not.Empty);

        // --- Assert: attaching a preview is not an edit. ---
        var stored = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.That(stored.TryGetProperty("editedAt", out var editedAt) &&
                    editedAt.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined),
            Is.False, "nobody edited this message, so it must not be marked as edited");

        // --- Assert: the crawler identified itself. ---
        Assert.That(_origin.UserAgents.Where(ua => ua is not null),
            Has.Some.Contains("EchoBot"));
    }

    [Test]
    public async Task PostingTheSameLinkTwice_FetchesTheOriginOnlyOnce()
    {
        // The property that keeps this instance from being an amplifier: one link seen by a
        // thousand people must remain one request to whoever published it.
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "cacheuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var channelId = await CreateChannelAsync(guild, "Cache Test Guild");

        var (firstId, _) = await SendAsync(messaging, channelId, $"first {_origin.PlainUrl}");
        await WaitForEmbedsAsync(messaging, channelId, firstId);

        var fetchesAfterFirst = _origin.CountRequestsFor("/plain");

        var (secondId, _) = await SendAsync(messaging, channelId, $"second {_origin.PlainUrl}");
        var secondEmbeds = await WaitForEmbedsAsync(messaging, channelId, secondId);

        Assert.Multiple(() =>
        {
            Assert.That(secondEmbeds, Has.Count.EqualTo(1), "the second message still gets its card");
            Assert.That(_origin.CountRequestsFor("/plain"), Is.EqualTo(fetchesAfterFirst),
                "the second unfurl must be served from Redis, not from the origin");
        });

        // Bare-HTML fallback: this page has no OG or Twitter tags at all.
        Assert.Multiple(() =>
        {
            Assert.That(StringOrNull(secondEmbeds[0], "title"), Is.EqualTo("Just A Title"));
            Assert.That(StringOrNull(secondEmbeds[0], "description"), Is.EqualTo("Just a description."));
            Assert.That(StringOrNull(secondEmbeds[0], "type"), Is.EqualTo("link"));
        });
    }

    [Test]
    public async Task SuppressingAPreview_RemovesItForEveryone()
    {
        var (_, authorToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "suppressauthor");
        using var guild = AuthedClient(_stack.Guild, authorToken);
        using var messaging = AuthedClient(_stack.Messaging, authorToken);

        var channelId = await CreateChannelAsync(guild, "Suppress Test Guild");

        var (messageId, _) = await SendAsync(messaging, channelId, $"dismiss me {_origin.ArticleUrl}");
        await WaitForEmbedsAsync(messaging, channelId, messageId);

        // --- Act ---

        var suppressResponse = await messaging.PatchAsJsonAsync(
            $"/api/v1/messaging/{messageId}/embeds", new { Suppress = true });
        await E2EAssert.SucceededAsync(suppressResponse, _stack.Messaging, "Suppress failed");

        // --- Assert: gone from the stored message, for anyone who reads it. ---

        var afterSuppress = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.That(ParseEmbeds(afterSuppress), Is.Empty, "the preview must be gone, not merely hidden for one viewer");

        // SUPPRESS_EMBEDS = 1 << 2. Clients need it to tell "dismissed" from "never generated" -
        // both otherwise look like an empty array.
        Assert.That(afterSuppress.GetProperty("flags").GetInt32() & (1 << 2), Is.Not.Zero);

        // --- Assert: it stays gone. ---
        await Task.Delay(TimeSpan.FromSeconds(5));
        var later = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.That(ParseEmbeds(later), Is.Empty, "a dismissed preview must not resurrect itself");

        // --- Act: undo. ---

        var restoreResponse = await messaging.PatchAsJsonAsync(
            $"/api/v1/messaging/{messageId}/embeds", new { Suppress = false });
        await E2EAssert.SucceededAsync(restoreResponse, _stack.Messaging, "Unsuppress failed");

        var restored = await WaitForEmbedsAsync(messaging, channelId, messageId);
        var afterRestore = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.Multiple(() =>
        {
            Assert.That(restored, Has.Count.EqualTo(1), "unsuppressing re-queues the unfurl");
            Assert.That(afterRestore.GetProperty("flags").GetInt32() & (1 << 2), Is.Zero);
        });
    }

    [Test]
    public async Task AStrangerCannotSuppressSomeoneElsesPreview()
    {
        var (_, authorToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "victimauthor");
        var (_, strangerToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "randomstranger");

        using var guild = AuthedClient(_stack.Guild, authorToken);
        using var messaging = AuthedClient(_stack.Messaging, authorToken);
        using var strangerMessaging = AuthedClient(_stack.Messaging, strangerToken);

        var channelId = await CreateChannelAsync(guild, "Permission Test Guild");
        var (messageId, _) = await SendAsync(messaging, channelId, $"mine {_origin.ArticleUrl}");
        await WaitForEmbedsAsync(messaging, channelId, messageId);

        var response = await strangerMessaging.PatchAsJsonAsync(
            $"/api/v1/messaging/{messageId}/embeds", new { Suppress = true });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "only the author, or a moderator holding DeleteAnyMessage, may dismiss a preview");

        var stillThere = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.That(ParseEmbeds(stillThere), Has.Count.EqualTo(1));
    }

    // ───────────────────────────────────────────────────────────────────────── Things that must
    // NOT be unfurled ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AngleBracketedLink_IsNeverFetched()
    {
        // <https://…> is the sender saying "link it, do not unfurl it".
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "quietuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var channelId = await CreateChannelAsync(guild, "Quiet Test Guild");

        var before = _origin.CountRequestsFor("/gone");
        var (messageId, _) = await SendAsync(messaging, channelId, $"no card please <{_origin.MissingUrl}>");

        await Task.Delay(TimeSpan.FromSeconds(8));

        var message = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.Multiple(() =>
        {
            Assert.That(ParseEmbeds(message), Is.Empty);
            Assert.That(_origin.CountRequestsFor("/gone"), Is.EqualTo(before),
                "the origin must never have been contacted at all");
        });
    }

    [Test]
    public async Task ALinkInsideACodeBlock_IsNeverFetched()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "codeuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var channelId = await CreateChannelAsync(guild, "Code Test Guild");

        var before = _origin.CountRequestsFor("/article");
        var (messageId, _) = await SendAsync(messaging, channelId, $"```\ncurl {_origin.ArticleUrl}\n```");

        await Task.Delay(TimeSpan.FromSeconds(8));

        var message = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.Multiple(() =>
        {
            Assert.That(ParseEmbeds(message), Is.Empty, "a pasted snippet is not a link to preview");
            Assert.That(_origin.CountRequestsFor("/article"), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task AMessageWithNoLinks_ProducesNothing()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "plainuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var channelId = await CreateChannelAsync(guild, "Plain Test Guild");
        var (messageId, _) = await SendAsync(messaging, channelId, "just some words, no links here");

        await Task.Delay(TimeSpan.FromSeconds(5));

        var message = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.That(ParseEmbeds(message), Is.Empty);
    }

    [Test]
    public async Task ADeadLink_LeavesTheMessageAloneRatherThanFailing()
    {
        // A 404 must degrade to "no card", not to a stuck message or a retry storm.
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "deadlinkuser");
        using var guild = AuthedClient(_stack.Guild, token);
        using var messaging = AuthedClient(_stack.Messaging, token);

        var channelId = await CreateChannelAsync(guild, "Dead Link Guild");
        var (messageId, _) = await SendAsync(messaging, channelId, $"broken {_origin.MissingUrl}");

        await Task.Delay(TimeSpan.FromSeconds(10));

        var message = await ReadMessageAsync(messaging, channelId, messageId);
        Assert.Multiple(() =>
        {
            Assert.That(ParseEmbeds(message), Is.Empty);
            Assert.That(ContentOf(message), Does.Contain(_origin.MissingUrl),
                "the message itself must be untouched");
        });
    }
}
