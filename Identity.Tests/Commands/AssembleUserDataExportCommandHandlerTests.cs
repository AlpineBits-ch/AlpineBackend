using AppEnvironment;
using Identity.Application.Commands;
using Identity.Application.Services.DataExport;
using Identity.Contracts.Bus.Commands;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Tests.Commands;

/// <summary>
/// The last step of T1-7, and specifically the terminal state it chooses (T1-7 / T1-9).
///
/// <para><b>What is under test is a claim, not a file.</b> <c>Echo.Sagas.ExportUserDataSaga</c> can
/// hand this handler a set of fragments that is missing whole services - because a participant
/// answered with an error, or because the saga's deadline elapsed and wrote a stand-in in its place.
/// Reporting that archive to the subject as <c>Ready</c> would be a false statement about a GDPR
/// Art. 15 disclosure: "here is everything we hold about you" when it demonstrably is not. So the
/// assertions here are about which status the row lands in, and about the missing services being
/// named on it rather than only buried in the archive's manifest.</para>
///
/// <para>Wolverine bus handler, so per this repo's convention it never calls
/// <c>SaveChangesAsync</c> itself - these tests call it afterwards to stand in for the transactional
/// middleware.</para>
/// </summary>
[TestFixture]
public class AssembleUserDataExportCommandHandlerTests
{
    /// <summary>Records the upload instead of talking to a bucket. The throwing variant is how the
    /// <c>Failed</c> path is reached without an unreachable S3 endpoint.</summary>
    private sealed class FakeArtifactStore(bool throwOnPut = false) : IDataExportArtifactStore
    {
        public List<string> Put { get; } = [];

        public Task PutAsync(string key, byte[] content, CancellationToken ct = default)
        {
            if (throwOnPut) throw new InvalidOperationException("bucket unavailable");
            Put.Add(key);
            return Task.CompletedTask;
        }

        public Task<string> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default) =>
            Task.FromResult($"https://signed.example/{key}");

        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static readonly string[] AllServices =
        ["bots", "federation", "guild", "identity", "import", "isle", "messaging", "social"];

    private async Task<ApplicationUser> SeedUserAsync(string tag)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"{tag}-{Guid.NewGuid():N}@example.com",
            Username = $"{tag}{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30)),
        });

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private async Task<DataExportRequest> SeedRunningRequestAsync(string userId)
    {
        var now = DateTimeOffset.UtcNow;
        var request = DataExportRequest.Create(userId, now);
        request.BeginRunning(now);

        _context.DataExportRequests.Add(request);
        await _context.SaveChangesAsync();
        return request;
    }

    private static UserDataExportFragment Good(string service) => new()
    {
        Service = service,
        FragmentJson = $$"""{"service":"{{service}}"}""",
        RowCounts = new Dictionary<string, int> { [service] = 1 },
    };

    /// <summary>Exactly what <c>ExportUserDataSaga.MissingFragment</c> writes when the deadline
    /// elapses with a service still silent.</summary>
    private static UserDataExportFragment Missing(string service) => new()
    {
        Service = service,
        Error = $"The {service} service did not respond within 01:00:00. This section of the export is missing.",
        RowCounts = new Dictionary<string, int>(),
        FragmentJson = $$"""{"error":"no answer from {{service}}","complete":false}""",
    };

    private Task HandleAsync(DataExportRequest request, IEnumerable<UserDataExportFragment> fragments, FakeArtifactStore store) =>
        AssembleUserDataExportCommandHandler.Handle(
            new AssembleUserDataExportCommand
            {
                ExportId = request.Id,
                UserId = request.UserId,
                Fragments = fragments.ToList(),
            },
            _context,
            store,
            NullLogger<AssembleUserDataExportCommandHandler>.Instance,
            CancellationToken.None);

    private async Task<DataExportRequest> ReloadAsync(string id)
    {
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return await _context.DataExportRequests.FirstAsync(r => r.Id == id);
    }

    // ── normal: every service answered ──────────────────────────────────────

    [Test]
    public async Task Handle_EveryServiceAnswered_IsReadyAndNamesNoMissingService()
    {
        var user = await SeedUserAsync("dxall");
        var request = await SeedRunningRequestAsync(user.Id);
        var store = new FakeArtifactStore();

        await HandleAsync(request, AllServices.Select(Good), store);

        var reloaded = await ReloadAsync(request.Id);

        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Ready));
            Assert.That(reloaded.MissingServices, Is.Empty);
            Assert.That(reloaded.FailureReason, Is.Null);
            Assert.That(reloaded.ArtifactKey, Is.Not.Null);
            Assert.That(reloaded.ExpiresAt, Is.EqualTo(reloaded.CompletedAt!.Value.Add(Env.DataExport.ArtifactTtl)));
            Assert.That(store.Put, Has.Count.EqualTo(1));
        });
    }

    // ── edge: some answered, some did not ───────────────────────────────────

    [Test]
    public async Task Handle_SomeServicesDidNotAnswer_IsPartialAndNamesThem()
    {
        var user = await SeedUserAsync("dxpart");
        var request = await SeedRunningRequestAsync(user.Id);
        var store = new FakeArtifactStore();

        var fragments = AllServices
            .Where(s => s is not ("bots" or "isle"))
            .Select(Good)
            .Concat([Missing("bots"), Missing("isle")]);

        await HandleAsync(request, fragments, store);

        var reloaded = await ReloadAsync(request.Id);

        Assert.Multiple(() =>
        {
            // Not Ready. "Complete" and "partial" are materially different answers to an Art. 15
            // request, and Status is the field that distinguishes them.
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Partial));

            // Which services, not how many - a subject told only that something is missing cannot
            // tell whether the gap matters for what they asked about.
            Assert.That(reloaded.MissingServices, Is.EqualTo(new[] { "bots", "isle" }));

            Assert.That(reloaded.FailureReason, Does.Contain("bots").And.Contain("isle"));
            Assert.That(reloaded.FailureReason, Does.Contain("incomplete"));

            // The archive still exists and is still downloadable - see the download test below.
            Assert.That(reloaded.ArtifactKey, Is.Not.Null);
            Assert.That(reloaded.IsDownloadable, Is.True);
            Assert.That(reloaded.ExpiresAt, Is.Not.Null);
            Assert.That(store.Put, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Handle_ServiceThatAnsweredWithAnError_CountsAsMissingToo()
    {
        var user = await SeedUserAsync("dxerr");
        var request = await SeedRunningRequestAsync(user.Id);

        // Not a deadline stand-in: messaging answered, and what it said was "I could not produce
        // this". The hole in the disclosure is exactly as large, so the status must be the same.
        var errored = Good("messaging");
        errored.Error = "The messaging export query timed out.";

        var fragments = AllServices.Where(s => s != "messaging").Select(Good).Concat([errored]);

        await HandleAsync(request, fragments, new FakeArtifactStore());

        var reloaded = await ReloadAsync(request.Id);

        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Partial));
            Assert.That(reloaded.MissingServices, Is.EqualTo(new[] { "messaging" }));
        });
    }

    [Test]
    public async Task Handle_RedeliveredAfterAPartial_CannotPromoteItToReady()
    {
        var user = await SeedUserAsync("dxredo");
        var request = await SeedRunningRequestAsync(user.Id);
        var store = new FakeArtifactStore();

        await HandleAsync(request, AllServices.Where(s => s != "isle").Select(Good).Concat([Missing("isle")]), store);
        await _context.SaveChangesAsync();

        // A retry that happens to carry a complete set must not turn an already-published partial
        // archive into one that claims to be complete - the object in the bucket is the partial one.
        await HandleAsync(request, AllServices.Select(Good), store);

        var reloaded = await ReloadAsync(request.Id);

        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Partial));
            Assert.That(reloaded.MissingServices, Is.EqualTo(new[] { "isle" }));
            Assert.That(store.Put, Has.Count.EqualTo(1), "the second assemble must not upload a second archive");
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_UploadThatThrows_IsFailedAndNotPartial()
    {
        var user = await SeedUserAsync("dxfail");
        var request = await SeedRunningRequestAsync(user.Id);

        await HandleAsync(request, AllServices.Where(s => s != "isle").Select(Good).Concat([Missing("isle")]),
            new FakeArtifactStore(throwOnPut: true));

        var reloaded = await ReloadAsync(request.Id);

        Assert.Multiple(() =>
        {
            // No archive was produced at all, so there is nothing partial about it. Failed is the
            // state that does not count against the rate limit and offers nothing to download.
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Failed));
            Assert.That(reloaded.ArtifactKey, Is.Null);
            Assert.That(reloaded.IsDownloadable, Is.False);
            Assert.That(reloaded.MissingServices, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_PartialNeverClaimsToBeReady_EvenWithASingleMissingService()
    {
        var user = await SeedUserAsync("dxone");
        var request = await SeedRunningRequestAsync(user.Id);

        await HandleAsync(request, AllServices.Where(s => s != "guild").Select(Good).Concat([Missing("guild")]),
            new FakeArtifactStore());

        var reloaded = await ReloadAsync(request.Id);

        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Status, Is.Not.EqualTo(DataExportStatus.Ready));
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Partial));
            // Singular wording, because "the guild services did not provide their sections" reads as
            // a bug to the person it is addressed to.
            Assert.That(reloaded.FailureReason, Does.Contain("service did not provide its section"));
        });
    }
}
