using Messaging.Domain.Entities;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Repositories;

/// <summary>
/// Covers the mention index over EF Core - the backend self-hosted deployments run.
/// </summary>
[TestFixture]
public class EfCoreMentionIndexRepositoryTests
{
    private const string UserId = "user-1";

    private static readonly DateTimeOffset Base = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private TestMessagingContext _context = null!;
    private EfCoreMentionIndexRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMentionIndexRepository(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static UserMention Mention(
        string messageId, DateTimeOffset createdAt, string userId = UserId,
        string? guildId = "gild-1", string kind = "Direct") => new()
    {
        UserId = userId,
        CreatedAt = createdAt,
        MessageId = messageId,
        ContextId = guildId is null ? "conv-1" : "chan-1",
        GuildId = guildId,
        ChannelId = guildId is null ? null : "chan-1",
        ConversationId = guildId is null ? "conv-1" : null,
        AuthorId = "user-2",
        Kind = kind,
    };

    private Task<IReadOnlyList<UserMention>> PageAsync(
        int limit = 25, DateTimeOffset? before = null, string? beforeId = null, DateTimeOffset? since = null) =>
        _repo.GetPageAsync(new MentionPageQuery
        {
            UserId = UserId, Limit = limit, Before = before, BeforeMessageId = beforeId, Since = since,
        });

    // ══════════════════════════════════════════════════════════════════════ Normal
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task InsertThenPage_ReturnsNewestFirst()
    {
        await _repo.AddAsync([
            Mention("mesg-a", Base.AddMinutes(-10)),
            Mention("mesg-b", Base.AddMinutes(-5)),
            Mention("mesg-c", Base),
        ]);

        var page = await PageAsync();

        Assert.That(page.Select(m => m.MessageId), Is.EqualTo(new[] { "mesg-c", "mesg-b", "mesg-a" }).AsCollection);
    }

    [Test]
    public async Task Page_OnlyReturnsTheCallersOwnMentions()
    {
        await _repo.AddAsync([
            Mention("mesg-mine", Base),
            Mention("mesg-theirs", Base, userId: "user-9"),
        ]);

        var page = await PageAsync();

        Assert.That(page.Select(m => m.MessageId), Is.EqualTo(new[] { "mesg-mine" }).AsCollection);
    }

    [Test]
    public async Task Insert_RoundTripsEveryField()
    {
        await _repo.AddAsync([Mention("mesg-a", Base, kind: "Here")]);

        var row = (await PageAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.GuildId, Is.EqualTo("gild-1"));
            Assert.That(row.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(row.AuthorId, Is.EqualTo("user-2"));
            Assert.That(row.Kind, Is.EqualTo("Here"));
        });
    }

    [Test]
    public async Task Delete_RemovesExactlyOneRow()
    {
        await _repo.AddAsync([Mention("mesg-a", Base), Mention("mesg-b", Base.AddMinutes(-1))]);

        await _repo.DeleteAsync(UserId, Base, "mesg-a");

        Assert.That((await PageAsync()).Select(m => m.MessageId), Is.EqualTo(new[] { "mesg-b" }).AsCollection);
    }

    // ══════════════════════════════════════════════════════════════════════ Edge
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task BeforeCursor_PagesBackwardsWithoutDuplicatingOrSkipping()
    {
        var mentions = Enumerable.Range(0, 9)
            .Select(i => Mention($"mesg-{i}", Base.AddMinutes(-i)))
            .ToList();
        await _repo.AddAsync(mentions);

        var seen = new List<string>();
        DateTimeOffset? before = null;
        string? beforeId = null;

        while (true)
        {
            var page = await PageAsync(limit: 4, before: before, beforeId: beforeId);
            if (page.Count == 0) break;

            seen.AddRange(page.Select(m => m.MessageId));
            before = page[^1].CreatedAt;
            beforeId = page[^1].MessageId;
        }

        Assert.Multiple(() =>
        {
            Assert.That(seen, Has.Count.EqualTo(9));
            Assert.That(seen.Distinct().Count(), Is.EqualTo(9));
        });
    }

    /// <summary>Two mentions can land in the same millisecond, which is why the cursor is a pair.
    /// Comparing the timestamp alone would either skip the second or return it twice forever.</summary>
    [Test]
    public async Task TwoMentionsInTheSameInstant_BothPageThroughExactlyOnce()
    {
        await _repo.AddAsync([
            Mention("mesg-a", Base),
            Mention("mesg-b", Base),
            Mention("mesg-c", Base.AddMinutes(-1)),
        ]);

        var first = await PageAsync(limit: 1);
        var second = await PageAsync(limit: 1, before: first[0].CreatedAt, beforeId: first[0].MessageId);
        var third = await PageAsync(limit: 1, before: second[0].CreatedAt, beforeId: second[0].MessageId);

        var seen = new[] { first[0].MessageId, second[0].MessageId, third[0].MessageId };
        Assert.That(seen.Distinct().Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task Since_ExcludesAnythingOlder()
    {
        await _repo.AddAsync([
            Mention("mesg-recent", Base),
            Mention("mesg-old", Base.AddDays(-10)),
        ]);

        var page = await PageAsync(since: Base.AddDays(-1));

        Assert.That(page.Select(m => m.MessageId), Is.EqualTo(new[] { "mesg-recent" }).AsCollection);
    }

    /// <summary>Postgres has no per-row TTL, so a row past the retention window can still be sitting
    /// there when the sweep is behind. Filtering on read is what keeps the two backends answering
    /// identically anyway.</summary>
    [Test]
    public async Task RowsOlderThanRetention_AreNotReturnedEvenBeforeTheSweepRuns()
    {
        await _repo.AddAsync([
            Mention("mesg-expired", DateTimeOffset.UtcNow - ScyllaMentionIndexRepository.Retention.Add(TimeSpan.FromDays(1))),
            Mention("mesg-live", DateTimeOffset.UtcNow.AddHours(-1)),
        ]);

        var page = await PageAsync(since: DateTimeOffset.UtcNow.AddYears(-5));

        Assert.That(page.Select(m => m.MessageId), Is.EqualTo(new[] { "mesg-live" }).AsCollection);
    }

    [Test]
    public async Task PurgeExpired_RemovesOnlyRowsPastRetention()
    {
        await _repo.AddAsync([
            Mention("mesg-expired", DateTimeOffset.UtcNow - ScyllaMentionIndexRepository.Retention.Add(TimeSpan.FromDays(1))),
            Mention("mesg-live", DateTimeOffset.UtcNow.AddHours(-1)),
        ]);

        var removed = await _repo.PurgeExpiredAsync();

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(_context.UserMentions.Count(), Is.EqualTo(1));
        });
    }

    /// <summary>The fan-out is a bus command and Wolverine retries it, so a replay must not double a
    /// mention - the Mentions tab counts these rows.</summary>
    [Test]
    public async Task AddingTheSameMentionTwice_IsIdempotent()
    {
        await _repo.AddAsync([Mention("mesg-a", Base)]);
        await _repo.AddAsync([Mention("mesg-a", Base)]);

        Assert.That(await PageAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AddingABatchWithInternalDuplicates_WritesOneRow()
    {
        await _repo.AddAsync([Mention("mesg-a", Base), Mention("mesg-a", Base)]);

        Assert.That(await PageAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DmMention_HasNoGuildId()
    {
        await _repo.AddAsync([Mention("mesg-dm", Base, guildId: null)]);

        var row = (await PageAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.GuildId, Is.Null);
            Assert.That(row.ConversationId, Is.EqualTo("conv-1"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Negative
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task EmptyBatch_IsANoOp()
    {
        Assert.DoesNotThrowAsync(() => _repo.AddAsync([]));

        Assert.That(await PageAsync(), Is.Empty);
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task NonPositiveLimit_IsClampedRatherThanPassedThrough(int limit)
    {
        await _repo.AddAsync([Mention("mesg-a", Base), Mention("mesg-b", Base.AddMinutes(-1))]);

        // Scylla treats LIMIT 0 as a hard error, so a clamp that let it through would turn an empty
        // page into a driver exception on the other backend.
        Assert.That(await PageAsync(limit: limit), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DeletingSomethingThatIsNotThere_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repo.DeleteAsync(UserId, Base, "mesg-never-existed"));
    }

    [Test]
    public async Task DeletingAnotherUsersMention_LeavesItAlone()
    {
        await _repo.AddAsync([Mention("mesg-a", Base, userId: "user-9")]);

        await _repo.DeleteAsync(UserId, Base, "mesg-a");

        Assert.That(_context.UserMentions.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task UserWithNoMentions_ReturnsAnEmptyPage()
    {
        Assert.That(await PageAsync(), Is.Empty);
    }
}
