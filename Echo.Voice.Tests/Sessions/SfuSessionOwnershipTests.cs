using Echo.Voice.Sessions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Echo.Voice.Tests.Sessions;

/// <summary>
/// Ownership is the check that stops a participant acting as somebody else's SFU session.
/// </summary>
[TestFixture]
public class SfuSessionOwnershipTests
{
    private IDistributedCache _cache = null!;
    private SfuSessionOwnership _sessions = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _sessions = new SfuSessionOwnership(_cache);
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task The_user_who_minted_a_session_owns_it()
    {
        await _sessions.BindAsync("cf-1", "user-a");

        Assert.That(await _sessions.OwnsAsync("cf-1", "user-a"), Is.True);
    }

    [Test]
    public async Task Release_drops_ownership()
    {
        await _sessions.BindAsync("cf-1", "user-a");
        await _sessions.ReleaseAsync("cf-1");

        Assert.That(await _sessions.OwnsAsync("cf-1", "user-a"), Is.False);
    }

    [Test]
    public async Task Sessions_are_tracked_independently()
    {
        await _sessions.BindAsync("cf-1", "user-a");
        await _sessions.BindAsync("cf-2", "user-b");

        Assert.Multiple(async () =>
        {
            Assert.That(await _sessions.OwnsAsync("cf-1", "user-a"), Is.True);
            Assert.That(await _sessions.OwnsAsync("cf-2", "user-b"), Is.True);
            Assert.That(await _sessions.OwnsAsync("cf-1", "user-b"), Is.False);
        });
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    /// <summary>The whole point: a co-participant holding the id must not be able to publish on,
    /// renegotiate, or tear down somebody else's session.</summary>
    [Test]
    public async Task A_different_user_does_not_own_a_session_they_merely_know_about()
    {
        await _sessions.BindAsync("cf-1", "user-a");

        Assert.That(await _sessions.OwnsAsync("cf-1", "user-b"), Is.False);
    }

    [Test]
    public async Task An_unknown_session_is_owned_by_nobody()
    {
        Assert.That(await _sessions.OwnsAsync("never-minted", "user-a"), Is.False);
    }

    [Test]
    public async Task A_blank_session_id_is_owned_by_nobody()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await _sessions.OwnsAsync(null, "user-a"), Is.False);
            Assert.That(await _sessions.OwnsAsync("", "user-a"), Is.False);
            Assert.That(await _sessions.OwnsAsync("   ", "user-a"), Is.False);
        });
    }

    [Test]
    public async Task Ownership_comparison_is_ordinal()
    {
        await _sessions.BindAsync("cf-1", "user-a");

        Assert.That(await _sessions.OwnsAsync("cf-1", "USER-A"), Is.False);
    }

}
