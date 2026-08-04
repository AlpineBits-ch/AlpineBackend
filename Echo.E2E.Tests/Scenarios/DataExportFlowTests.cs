using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;
using Npgsql;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves the "give me a copy of my data" flow end to end over real infrastructure (GDPR Art. 15/20,
/// T1-7 of docs/specs/privacy.md): Identity's <c>DataExportController</c> commits a request row and
/// publishes <c>DataExportRequestedEvent</c> over real RabbitMQ, Echo's <c>ExportUserDataSaga</c>
/// fans <c>ExportUserDataCommand</c> out to every participating service, each running one does its
/// real Postgres read and answers with a fragment, the saga's deadline resolves the ones that never
/// answer, and Identity's <c>AssembleUserDataExportCommandHandler</c> zips the result, uploads it to
/// real S3-compatible object storage and flips the row. Nothing is mocked or short-circuited.
///
/// <para><b>The point of this fixture is that it names participants individually.</b> This feature
/// shipped with no E2E coverage on the argument that the harness cannot spawn Bots or Isle, so an
/// export started here can never reach <c>MarkCompleted</c>. That argument confused completion with
/// participation. Six of the eight participants - identity, social, guild, messaging, federation,
/// import - do run here, and the failure that actually reached production was one of them receiving
/// the fan-out and silently never replying. An assertion that "some fragments arrived" would sail
/// straight past that; <see cref="EachRunningServiceContributesItsOwnFragment"/> asserts each of the
/// six by name, so one service going quiet is a red test naming that service.</para>
///
/// <para><b>Bots and Isle being absent is the fixture, not a gap to work around.</b> Their silence is
/// the only non-participant this harness can produce, and it is what lets the deadline path be
/// exercised at all: the export resolves <c>Partial</c> naming exactly <c>bots</c> and <c>isle</c>,
/// which is simultaneously the assertion that the saga's deadline resolves rather than hangs, and the
/// assertion that it does not over- or under-name who was missing. See EchoTestStack for why neither
/// can be spawned yet.</para>
///
/// <para><b>Two harness prerequisites, both solved without weakening an assertion.</b> The saga's
/// one-hour deadline is shrunk to 30 seconds for spawned processes via
/// <c>DATA_EXPORT_SAGA_DEADLINE_SECONDS</c> (see EchoTestStack; the production default is
/// untouched), and the assembler's upload target is a real MinIO container in the shared
/// <see cref="EchoInfraSet"/> rather than a substituted artifact store - substituting it would have
/// meant a test-only seam in production DI, and "the archive is downloadable" is precisely an
/// assertion about the round trip through the bucket and the signed URL.</para>
/// </summary>
[TestFixture]
[Category("E2E")]
public class DataExportFlowTests
{
    private const string ExportsUrl = "/api/v1/data-exports";

    /// <summary>Long enough to cover the 30s saga deadline plus the assemble/upload round trip, and
    /// still short enough that a genuinely stuck export fails the run rather than hanging it.</summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Every participant in <c>ExportUserDataSaga.ParticipatingServices</c> that
    /// <see cref="EchoTestStack"/> actually spawns. Each one must produce its own fragment.</summary>
    private static readonly string[] RunningServices =
        ["identity", "social", "guild", "messaging", "federation", "import"];

    /// <summary>The participants this harness deliberately does not spawn. Exactly these - no more,
    /// no fewer - must come back named as missing.</summary>
    private static readonly string[] AbsentServices = ["bots", "isle"];

    private EchoTestStack _stack = null!;

    /// <summary>Set on the subject and on the bystander alike, so the "no third-party phone number"
    /// assertion has something to find if the rule is ever broken. There is no API for this - a phone
    /// number is set through a verification flow this test has no reason to drive - so it is arranged
    /// directly against Identity's database, the same way AccountDeletionFlowTests asserts against
    /// it.
    ///
    /// <para>Digits only, with no leading <c>+</c>. System.Text.Json's default encoder escapes
    /// <c>+</c> to <c>+</c>, so a number written as <c>+1555...</c> would be in the archive
    /// under a spelling a substring search for <c>+1555...</c> never matches - the leak assertion
    /// would pass while the leak was there, and the "the subject's own number is present" control
    /// that is supposed to catch exactly that would have to be dropped to keep the test green.</para>
    /// </summary>
    private const string SubjectPhoneNumber = "15550001111";
    private const string BystanderPhoneNumber = "15550002222";

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "dxport", "dxport-test-instance");
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

    /// <summary>
    /// A client that does <b>not</b> follow redirects, because the download route's answer is the
    /// redirect: a 302 whose Location is a short-lived signed URL. An auto-following client would
    /// turn "Identity signed a URL" and "the object is really in the bucket" into one indistinct
    /// success or failure.
    /// </summary>
    private static HttpClient NonRedirectingClient(SpawnedServiceProcess service, string token)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = service.Client.BaseAddress,
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string IdentityConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = EchoInfraFixture.Default.PostgresHost,
        Port = EchoInfraFixture.Default.PostgresPort,
        Database = _stack.IdentityDatabaseName,
        Username = "postgres",
        Password = "postgres",
    }.ConnectionString;

    /// <summary>Registration mints the address itself, so the only way to know what to search the
    /// archive for is to ask Identity's database what it stored.</summary>
    private async Task<string> EmailOfAsync(string userId)
    {
        await using var connection = new NpgsqlConnection(IdentityConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT email FROM asp_net_users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);

        var email = await command.ExecuteScalarAsync() as string;
        Assert.That(email, Is.Not.Null.And.Not.Empty, $"No email row for {userId}");
        return email!;
    }

    private async Task SetPhoneNumberAsync(string userId, string phoneNumber)
    {
        await using var connection = new NpgsqlConnection(IdentityConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "UPDATE asp_net_users SET phone_number = @phone WHERE id = @id", connection);
        command.Parameters.AddWithValue("phone", phoneNumber);
        command.Parameters.AddWithValue("id", userId);

        Assert.That(await command.ExecuteNonQueryAsync(), Is.EqualTo(1),
            $"Could not arrange a phone number on {userId}");
    }

    private static async Task<string> RequestExportAsync(HttpClient identity, SpawnedServiceProcess service)
    {
        var response = await identity.PostAsync(ExportsUrl, null);
        await E2EAssert.HasStatusAsync(response, HttpStatusCode.Accepted, service, "Requesting a data export");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("exportId").GetString()!;
    }

    private static async Task<JsonElement> ReadExportRowAsync(
        HttpClient identity, SpawnedServiceProcess service, string exportId)
    {
        var response = await identity.GetAsync(ExportsUrl);
        await E2EAssert.SucceededAsync(response, service, "Listing data exports");

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = rows.EnumerateArray().FirstOrDefault(r => r.GetProperty("exportId").GetString() == exportId);

        Assert.That(row.ValueKind, Is.Not.EqualTo(JsonValueKind.Undefined),
            $"Export {exportId} is not in the caller's own list.");

        return row.Clone();
    }

    /// <summary>
    /// Polls until the request leaves <c>Pending</c>/<c>Running</c>.
    ///
    /// <para>Failing here rather than letting a caller time out on some later assertion is
    /// deliberate. A participant whose queue never got bound is invisible - Wolverine derives queue
    /// names from the handler type and a fanout exchange drops a message no binding matches, with no
    /// error raised anywhere - so the only symptom is an export that does not finish. This says that
    /// plainly, with the status it was last seen in and the gateway's own log, instead of an
    /// ambiguous hang.</para>
    /// </summary>
    private async Task<JsonElement> WaitForResolutionAsync(
        HttpClient identity, string exportId)
    {
        var deadline = DateTime.UtcNow + ResolveTimeout;
        var lastStatus = "(never observed)";

        while (DateTime.UtcNow < deadline)
        {
            var row = await ReadExportRowAsync(identity, _stack.Identity, exportId);
            lastStatus = row.GetProperty("status").GetString()!;

            if (lastStatus is not ("Pending" or "Running")) return row;

            await Task.Delay(500);
        }

        Assert.Fail(
            $"Data export {exportId} never left '{lastStatus}' within {ResolveTimeout}. The saga either "
            + "never started (no DataExportRequestedEvent reached the gateway) or never resolved - note "
            + "that its deadline is only 30s here, so this is a broken fan-out rather than a slow one.\n"
            + $"--- gateway ---\n{_stack.Gateway.CapturedOutput}\n"
            + $"--- identity ---\n{_stack.Identity.CapturedOutput}");

        return default;
    }

    /// <summary>Follows the download route's 302 and returns every file in the archive, decompressed,
    /// keyed by name.</summary>
    private async Task<Dictionary<string, string>> DownloadArchiveAsync(string token, string exportId)
    {
        using var identity = NonRedirectingClient(_stack.Identity, token);

        var response = await identity.GetAsync($"{ExportsUrl}/{exportId}/download");

        // Not "Ready", and downloadable anyway - a client that gated on Ready would hide an archive
        // the subject is entitled to. See DataExportRequest.IsDownloadable.
        await E2EAssert.HasStatusAsync(
            response, HttpStatusCode.Found, _stack.Identity, "Downloading a Partial export's archive");

        var location = response.Headers.Location;
        Assert.That(location, Is.Not.Null, "The download route redirected without a Location header.");

        using var storage = new HttpClient();

        HttpResponseMessage archiveResponse;
        try
        {
            archiveResponse = await storage.GetAsync(location);
        }
        catch (HttpRequestException e)
        {
            // The URL itself, not just the exception: everything that can go wrong between Identity
            // and the bucket - wrong scheme, wrong host, wrong port - is visible in it and invisible
            // in "the SSL connection could not be established".
            Assert.Fail($"The signed URL Identity handed back could not be fetched at all.\n"
                        + $"URL: {location}\n{e}");
            throw;
        }

        Assert.That(archiveResponse.IsSuccessStatusCode, Is.True,
            $"The signed URL Identity handed back did not serve the archive "
            + $"({(int)archiveResponse.StatusCode} {archiveResponse.StatusCode}): "
            + await archiveResponse.Content.ReadAsStringAsync());

        var bytes = await archiveResponse.Content.ReadAsByteArrayAsync();
        Assert.That(bytes, Is.Not.Empty, "The archive downloaded as zero bytes.");

        using var buffer = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[entry.FullName] = await reader.ReadToEndAsync();
        }

        return files;
    }

    /// <summary>The archive's manifest, indexed by service - the one place that records, per service,
    /// whether its section is present or why it is not.</summary>
    private static Dictionary<string, JsonElement> ManifestServices(Dictionary<string, string> files)
    {
        Assert.That(files.ContainsKey("manifest.json"), Is.True,
            $"The archive has no manifest.json. Files: {string.Join(", ", files.Keys)}");

        using var manifest = JsonDocument.Parse(files["manifest.json"]);

        return manifest.RootElement.GetProperty("services")
            .EnumerateArray()
            .ToDictionary(s => s.GetProperty("service").GetString()!, s => s.Clone(), StringComparer.Ordinal);
    }

    private static int RowCount(JsonElement manifestEntry, string collection) =>
        manifestEntry.GetProperty("rowCounts").TryGetProperty(collection, out var value) ? value.GetInt32() : -1;

    /// <summary>
    /// Arranges an account that every participant holds something about - a guild it owns, an
    /// accepted friendship, a DM it authored - plus a second account that must not appear.
    /// </summary>
    private async Task<(string SubjectId, string SubjectToken, string BystanderId)> ArrangeAsync(string prefix)
    {
        var (subjectId, subjectToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, $"{prefix}_a");
        var (bystanderId, bystanderToken) = await E2EUsers.RegisterAndGetTokenAsync(_stack, $"{prefix}_b");

        using var guildA = AuthedClient(_stack.Guild, subjectToken);
        using var guildB = AuthedClient(_stack.Guild, bystanderToken);
        using var socialA = AuthedClient(_stack.Social, subjectToken);
        using var socialB = AuthedClient(_stack.Social, bystanderToken);
        using var messagingA = AuthedClient(_stack.Messaging, subjectToken);

        var createGuild = await guildA.PostAsJsonAsync("/api/v1/guilds", new { Name = "Export Test Guild" });
        await E2EAssert.SucceededAsync(createGuild, _stack.Guild, "Create guild");
        var guildId = (await createGuild.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var createInvite = await guildA.PostAsJsonAsync($"/api/v1/guilds/{guildId}/invite", new { Type = "OneTime" });
        await E2EAssert.SucceededAsync(createInvite, _stack.Guild, "Create invite");
        var inviteId = (await createInvite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var redeem = await guildB.PostAsync($"/api/v1/invites/{inviteId}/redeem", null);
        await E2EAssert.SucceededAsync(redeem, _stack.Guild, "Redeem invite");

        var bProfile = await socialA.GetAsync($"/api/v1/profiles/by-user/{bystanderId}");
        await E2EAssert.SucceededAsync(bProfile, _stack.Social, "Fetch the bystander's profile");
        var bUserName = (await bProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userName").GetString();

        var friendRequest = await socialA.PostAsJsonAsync("/api/v1/relationships", new { UserName = bUserName, Hash = 0 });
        await E2EAssert.SucceededAsync(friendRequest, _stack.Social, "Friend request");

        var bRelationships = await socialB.GetAsync("/api/v1/relationships");
        await E2EAssert.SucceededAsync(bRelationships, _stack.Social, "List relationships");
        var pendingIncomingId = (await bRelationships.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray()
            .First(r => r.GetProperty("status").GetString() == "PendingIncoming")
            .GetProperty("id").GetString()!;

        var accept = await socialB.PostAsync($"/api/v1/relationships/{pendingIncomingId}/accept", null);
        await E2EAssert.SucceededAsync(accept, _stack.Social, "Accept friend request");

        var createConversation = await messagingA.PostAsJsonAsync("/api/v1/conversations", new
        {
            Encryption = "Plain",
            Members = new[] { new { UserId = bystanderId } },
        });
        await E2EAssert.SucceededAsync(createConversation, _stack.Messaging, "Create conversation");
        var conversationId = (await createConversation.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var sendMessage = await messagingA.PostAsJsonAsync("/api/v1/messaging", new
        {
            Content = "a message that belongs in my export",
            ConversationId = conversationId,
        });
        await E2EAssert.SucceededAsync(sendMessage, _stack.Messaging, "Send message");

        await SetPhoneNumberAsync(subjectId, SubjectPhoneNumber);
        await SetPhoneNumberAsync(bystanderId, BystanderPhoneNumber);

        return (subjectId, subjectToken, bystanderId);
    }

    // ── the whole flow ──────────────────────────────────────────────────────

    /// <summary>
    /// One export, from the request to the downloaded zip, asserting every claim the feature makes
    /// along the way. Written as one test rather than five because each of those claims is about the
    /// same single artifact, and producing it costs a real 30-second saga deadline - five exports
    /// would be five deadlines to prove things about five identical archives.
    /// </summary>
    [Test]
    public async Task EachRunningServiceContributesItsOwnFragment()
    {
        var (subjectId, subjectToken, bystanderId) = await ArrangeAsync("dxport");

        var subjectEmail = await EmailOfAsync(subjectId);
        var bystanderEmail = await EmailOfAsync(bystanderId);

        using var identity = AuthedClient(_stack.Identity, subjectToken);

        var exportId = await RequestExportAsync(identity, _stack.Identity);

        // ── the rate limit, while one is outstanding ────────────────────────
        //
        // Asserted here rather than in its own test because "outstanding" is a window that only
        // exists between the request and the deadline, and this is standing in it.
        var refused = await identity.PostAsync(ExportsUrl, null);
        await E2EAssert.HasStatusAsync(
            refused, HttpStatusCode.TooManyRequests, _stack.Identity,
            "A second export requested while the first is still running");
        var refusal = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(refusal.GetProperty("code").GetString(), Is.EqualTo("data_export_rate_limited"));
            Assert.That(refusal.GetProperty("retryAfterSeconds").GetInt32(), Is.GreaterThan(0));
            Assert.That(refused.Headers.RetryAfter, Is.Not.Null);
        });

        // ── the deadline resolves it, rather than leaving it Running ────────

        var row = await WaitForResolutionAsync(identity, exportId);
        var status = row.GetProperty("status").GetString();
        var missing = row.GetProperty("missingServices").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.That(status, Is.EqualTo("Partial"),
            $"Expected the deadline to resolve this export as Partial (bots and isle are not spawned "
            + $"here), got '{status}' with failureReason '{row.GetProperty("failureReason")}'.\n"
            + $"--- gateway ---\n{_stack.Gateway.CapturedOutput}");

        // Exactly the two that are not running. Over-naming would mean a service that did answer was
        // recorded as silent; under-naming would mean a silent service was passed off as complete.
        Assert.That(missing, Is.EqualTo(AbsentServices),
            "The Partial export must name exactly the participants that did not answer. Anything else "
            + "here means a running service went quiet (extra name) or a silent one was counted as "
            + $"having answered (missing name).\n--- gateway ---\n{_stack.Gateway.CapturedOutput}");

        Assert.That(row.GetProperty("expiresAt").ValueKind, Is.Not.EqualTo(JsonValueKind.Null));

        // ── the archive is downloadable while Partial ───────────────────────

        var files = await DownloadArchiveAsync(subjectToken, exportId);
        var manifest = ManifestServices(files);

        // ── every running participant answered, by name ─────────────────────
        //
        // The assertion this whole fixture exists for. A service that never answered still gets a
        // file and a manifest entry - ExportUserDataSaga.MissingFragment writes a stand-in on purpose,
        // so that "holds nothing about me" stays distinguishable from "did not answer" - so presence
        // is not the test. A null error is.
        Assert.Multiple(() =>
        {
            foreach (var service in RunningServices)
            {
                Assert.That(files.ContainsKey($"{service}.json"), Is.True,
                    $"The archive has no {service}.json. Files: {string.Join(", ", files.Keys)}");
                Assert.That(manifest.ContainsKey(service), Is.True,
                    $"manifest.json does not mention {service}.");

                if (!manifest.TryGetValue(service, out var entry)) continue;

                Assert.That(entry.GetProperty("error").ValueKind, Is.EqualTo(JsonValueKind.Null),
                    $"{service} is running in this harness but its section came back with an error - "
                    + $"either it never answered the fan-out and the deadline wrote a stand-in for it, "
                    + $"or its handler failed: {entry.GetProperty("error")}");
            }
        });

        // Not just "answered", but "answered with this account's real rows". Federation is exempt by
        // design: it holds nothing keyed directly to a user (see its handler) and reports no counts.
        Assert.Multiple(() =>
        {
            Assert.That(RowCount(manifest["identity"], "account"), Is.EqualTo(1));
            Assert.That(RowCount(manifest["social"], "relationships"), Is.GreaterThanOrEqualTo(1),
                "Social answered but without the friendship arranged for this export.");
            Assert.That(RowCount(manifest["guild"], "memberships"), Is.GreaterThanOrEqualTo(1),
                "Guild answered but without the membership of the guild this account owns.");
            Assert.That(RowCount(manifest["messaging"], "messages"), Is.GreaterThanOrEqualTo(1),
                "Messaging answered but without the message this account authored.");
            Assert.That(RowCount(manifest["import"], "importJobs"), Is.GreaterThanOrEqualTo(0),
                "Import answered without its importJobs count.");
            Assert.That(files["federation.json"], Does.Contain("federated"),
                "Federation's section is not the notice its handler produces.");
        });

        // ── and the two that are not running are recorded as absent ─────────

        Assert.Multiple(() =>
        {
            foreach (var service in AbsentServices)
            {
                Assert.That(files.ContainsKey($"{service}.json"), Is.True,
                    $"A service that did not answer must still get a section saying so - an absent file "
                    + $"reads as 'holds nothing about me'. {service}.json is missing.");
                Assert.That(manifest[service].GetProperty("error").ValueKind, Is.EqualTo(JsonValueKind.String),
                    $"{service} is not running here, so its manifest entry must carry an error.");
                Assert.That(files[$"{service}.json"], Does.Contain("\"complete\": false"));
            }
        });

        // ── no third-party personal data anywhere in the archive ────────────
        //
        // There is a per-participant unit guarantee for this already; this is the one end-to-end
        // check that the assembled zip - every fragment, plus the manifest - holds it. The subject's
        // own address and number are asserted present first: without that, a search that silently
        // matched nothing would pass this test while the archive leaked everything.
        var archiveText = string.Join("\n", files.Values);

        Assert.Multiple(() =>
        {
            Assert.That(archiveText, Does.Contain(subjectEmail),
                "The subject's own email is absent, so the searches below prove nothing.");
            Assert.That(archiveText, Does.Contain(SubjectPhoneNumber),
                "The subject's own phone number is absent, so the searches below prove nothing.");

            Assert.That(archiveText, Does.Not.Contain(bystanderEmail),
                "Another account's email address appears in the archive. Other people are opaque ids "
                + "in an export - see ExportUserDataResponse's contract.");
            Assert.That(archiveText, Does.Not.Contain(BystanderPhoneNumber),
                "Another account's phone number appears in the archive.");
        });

        // ── a Partial does not consume the 24h window ───────────────────────
        //
        // A partial export is our fault wearing the shape of an answer; charging it against the limit
        // would cost the subject a statutory day AND leave them holding an incomplete disclosure.
        var again = await identity.PostAsync(ExportsUrl, null);
        await E2EAssert.HasStatusAsync(
            again, HttpStatusCode.Accepted, _stack.Identity,
            "Re-requesting an export immediately after a Partial one");

        var secondExportId = (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("exportId").GetString();
        Assert.That(secondExportId, Is.Not.EqualTo(exportId));
    }

    // ── negative ────────────────────────────────────────────────────────────

    /// <summary>
    /// The download route while the fan-out is still in flight. A separate, cheap test because it is
    /// about the window before any archive exists - the one moment
    /// <see cref="EachRunningServiceContributesItsOwnFragment"/> deliberately waits out.
    /// </summary>
    [Test]
    public async Task DownloadBeforeAnythingHasBeenAssembledIs409()
    {
        var (_, token) = await E2EUsers.RegisterAndGetTokenAsync(_stack, "dxport_c");

        using var identity = AuthedClient(_stack.Identity, token);
        using var nonRedirecting = NonRedirectingClient(_stack.Identity, token);

        var exportId = await RequestExportAsync(identity, _stack.Identity);

        var response = await nonRedirecting.GetAsync($"{ExportsUrl}/{exportId}/download");
        await E2EAssert.HasStatusAsync(
            response, HttpStatusCode.Conflict, _stack.Identity,
            "Downloading an export that has not been assembled yet");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("data_export_not_ready"));
    }
}
