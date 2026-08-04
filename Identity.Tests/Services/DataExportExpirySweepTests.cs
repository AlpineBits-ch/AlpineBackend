using AppEnvironment;
using Identity.Application.Services.DataExport;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Services;

/// <summary>
/// The seven-day half of T1-7: an archive stops being downloadable, the object is deleted, and the
/// row that records the request survives both.
/// </summary>
[TestFixture]
public class DataExportExpirySweepTests
{
    /// <summary>Records what was deleted instead of talking to a bucket. The failing variant exists
    /// because "one stubborn key must not stop every key behind it" is a behaviour, not a
    /// comment.</summary>
    private sealed class FakeArtifactStore(bool throwOnDelete = false) : IDataExportArtifactStore
    {
        public List<string> Deleted { get; } = [];
        public List<string> Put { get; } = [];

        public Task PutAsync(string key, byte[] content, CancellationToken ct = default)
        {
            Put.Add(key);
            return Task.CompletedTask;
        }

        public Task<string> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default) =>
            Task.FromResult($"https://signed.example/{key}");

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            if (throwOnDelete) throw new InvalidOperationException("bucket unavailable");
            Deleted.Add(key);
            return Task.CompletedTask;
        }
    }

    private TestIdentityContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestIdentityContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<ApplicationUser> SeedUserAsync()
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"sweep-{Guid.NewGuid():N}@example.com",
            Username = $"sweep{Guid.NewGuid():N}"[..12],
            PhoneNumber = null!,
            BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30)),
        });

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private async Task<DataExportRequest> SeedReadyAsync(string userId, DateTimeOffset completedAt, TimeSpan ttl)
    {
        var request = DataExportRequest.Create(userId, completedAt);
        request.MarkReady(DataExportRequest.NewArtifactKey(request.Id), completedAt, ttl);

        _context.DataExportRequests.Add(request);
        await _context.SaveChangesAsync();
        return request;
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Run_ExpiredArtifact_IsDeletedAndTheRowSurvives()
    {
        var user = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        var request = await SeedReadyAsync(user.Id, now.AddDays(-8), TimeSpan.FromDays(7));
        var key = request.ArtifactKey!;

        var store = new FakeArtifactStore();
        var result = await DataExportExpirySweep.RunAsync(_context, store, now);

        var reloaded = await _context.DataExportRequests.FirstAsync(r => r.Id == request.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.ArtifactsDeleted, Is.EqualTo(1));
            Assert.That(result.RowsExpired, Is.EqualTo(1));
            Assert.That(store.Deleted, Does.Contain(key));

            // The row is the record that the subject exercised an access right and until when it
            // was answered. Only the artifact goes.
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Expired));
            Assert.That(reloaded.ArtifactKey, Is.Null);
            Assert.That(reloaded.RequestedAt, Is.Not.EqualTo(default(DateTimeOffset)));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Run_ArtifactStillInsideItsWindow_IsLeftAlone()
    {
        var user = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        var request = await SeedReadyAsync(user.Id, now.AddDays(-1), TimeSpan.FromDays(7));

        var store = new FakeArtifactStore();
        var result = await DataExportExpirySweep.RunAsync(_context, store, now);

        var reloaded = await _context.DataExportRequests.FirstAsync(r => r.Id == request.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.RowsExpired, Is.EqualTo(0));
            Assert.That(store.Deleted, Is.Empty);
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Ready));
        });
    }

    [Test]
    public async Task Run_ExpiredPartialArchive_IsSweptExactlyLikeACompleteOne()
    {
        var user = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;

        var request = DataExportRequest.Create(user.Id, now.AddDays(-8));
        request.MarkPartial(
            DataExportRequest.NewArtifactKey(request.Id), ["bots", "isle"], now.AddDays(-8), TimeSpan.FromDays(7));

        _context.DataExportRequests.Add(request);
        await _context.SaveChangesAsync();

        var key = request.ArtifactKey!;
        var store = new FakeArtifactStore();
        var result = await DataExportExpirySweep.RunAsync(_context, store, now);

        var reloaded = await _context.DataExportRequests.FirstAsync(r => r.Id == request.Id);

        Assert.Multiple(() =>
        {
            // A partial archive is short a section, not empty - it is a real object holding real
            // personal data. Leaving Partial out of the sweep's query would mean every export that
            // came back incomplete lived in the bucket forever.
            Assert.That(result.ArtifactsDeleted, Is.EqualTo(1));
            Assert.That(store.Deleted, Does.Contain(key));
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Expired));
            Assert.That(reloaded.ArtifactKey, Is.Null);

            // The record of what was missing outlives the artifact, exactly as the row does.
            Assert.That(reloaded.MissingServices, Is.EqualTo(new[] { "bots", "isle" }));
        });
    }

    [Test]
    public async Task Run_RunningRequestWithNoArtifact_IsNotTouched()
    {
        var user = await SeedUserAsync();
        var request = DataExportRequest.Create(user.Id, DateTimeOffset.UtcNow.AddDays(-30));
        request.BeginRunning(DateTimeOffset.UtcNow.AddDays(-30));
        _context.DataExportRequests.Add(request);
        await _context.SaveChangesAsync();

        var result = await DataExportExpirySweep.RunAsync(_context, new FakeArtifactStore(), DateTimeOffset.UtcNow);

        var reloaded = await _context.DataExportRequests.FirstAsync(r => r.Id == request.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.RowsExpired, Is.EqualTo(0));
            Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Running));
        });
    }

    [Test]
    public async Task Run_Twice_IsIdempotent()
    {
        var user = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedReadyAsync(user.Id, now.AddDays(-8), TimeSpan.FromDays(7));

        var store = new FakeArtifactStore();
        await DataExportExpirySweep.RunAsync(_context, store, now);
        var second = await DataExportExpirySweep.RunAsync(_context, store, now);

        Assert.Multiple(() =>
        {
            Assert.That(second.RowsExpired, Is.EqualTo(0));
            Assert.That(store.Deleted, Has.Count.EqualTo(1));
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Run_StoreThatThrowsOnDelete_AbortsThePassRatherThanForgettingTheKey()
    {
        var user = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        var request = await SeedReadyAsync(user.Id, now.AddDays(-8), TimeSpan.FromDays(7));

        // The real S3 store swallows and logs its own delete failures - see
        // S3DataExportArtifactStore. A store that propagates instead must not leave the row marked
        // Expired with its key cleared, because nothing would ever find the object again.
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataExportExpirySweep.RunAsync(_context, new FakeArtifactStore(throwOnDelete: true), now));

        _context.ChangeTracker.Clear();
        var reloaded = await _context.DataExportRequests.FirstAsync(r => r.Id == request.Id);

        Assert.That(reloaded.ArtifactKey, Is.Not.Null);
        Assert.That(reloaded.Status, Is.EqualTo(DataExportStatus.Ready));
    }

    [Test]
    public void Configuration_ArtifactTtlDefaultsToSevenDays()
    {
        // The number T1-7 and T1-8 both name. Asserted because it is the promise the download route
        // enforces and the sweep acts on.
        Assert.That(Env.DataExport.ArtifactTtl, Is.EqualTo(TimeSpan.FromDays(7)));
    }
}
