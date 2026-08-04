using Guild.Application.Bus.Consumers;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// Covers <see cref="GetSharedGuildsHandler"/> - the contract Social resolves
/// <c>FriendRequestPolicy.ServerMembers</c> (T2-15) and <c>mutualServers</c> (T2-17) against.
///
/// <para>Two families of assertion here, and the second is the important one. The first is that the
/// intersection is right. The second is that this cannot be turned into a roster: co-membership is
/// the fact <c>MutualServersVisibility</c> exists to protect, so a contract that answers a
/// too-broad question is a leak whatever the caller does with it.</para>
/// </summary>
[TestFixture]
public class GetSharedGuildsHandlerTests
{
    private const string SharedGuildA = "gild-shared-a";
    private const string SharedGuildB = "gild-shared-b";
    private const string ViewerOnlyGuild = "gild-viewer-only";
    private const string SubjectOnlyGuild = "gild-subject-only";

    private const string ViewerId = "user-viewer";
    private const string SubjectId = "user-subject";
    private const string StrangerId = "user-stranger";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();

        foreach (var guildId in new[] { SharedGuildA, SharedGuildB, ViewerOnlyGuild, SubjectOnlyGuild })
        {
            _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
            {
                Id = guildId, Name = "g", OwnerId = "owner-1",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        AddMember(SharedGuildA, ViewerId);
        AddMember(SharedGuildA, SubjectId);
        AddMember(SharedGuildB, ViewerId);
        AddMember(SharedGuildB, SubjectId);
        AddMember(ViewerOnlyGuild, ViewerId);
        AddMember(SubjectOnlyGuild, SubjectId);
        AddMember(SubjectOnlyGuild, StrangerId);

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private void AddMember(string guildId, string userId) =>
        _context.GuildMembers.Add(new GuildMember
        {
            Id = $"memb-{guildId}-{userId}", GuildId = guildId, UserId = userId,
            JoinedAt = DateTime.UtcNow, SearchValue = userId.ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

    private Task<Guild.Contracts.Bus.Response.GetSharedGuildsResponse> RunAsync(
        string userId, string[] others, params (string Blocker, string Blocked)[] blocks) =>
        GetSharedGuildsHandler.Handle(
            new GetSharedGuildsRequest { UserId = userId, OtherUserIds = others },
            _context,
            PrivacyTestFactory.Blocks(_bus, _cache, blocks));

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ReturnsOnlyTheGuildsBothAreIn()
    {
        var response = await RunAsync(ViewerId, [SubjectId]);

        var summary = response.Shared.Single();
        Assert.Multiple(() =>
        {
            Assert.That(summary.OtherUserId, Is.EqualTo(SubjectId));
            Assert.That(summary.GuildIds, Is.EquivalentTo(new[] { SharedGuildA, SharedGuildB }));
            Assert.That(summary.GuildIds, Does.Not.Contain(ViewerOnlyGuild));
            Assert.That(summary.GuildIds, Does.Not.Contain(SubjectOnlyGuild));
        });
    }

    [Test]
    public async Task Handle_IsBatched_AnsweringForSeveralPeopleAtOnce()
    {
        var response = await RunAsync(ViewerId, [SubjectId, StrangerId]);

        // The viewer shares nothing with the stranger, so the stranger is absent entirely.
        Assert.That(response.Shared.Select(s => s.OtherUserId), Is.EquivalentTo(new[] { SubjectId }));
    }

    [Test]
    public async Task Handle_IsSymmetric_FromEitherSide()
    {
        var fromViewer = await RunAsync(ViewerId, [SubjectId]);
        var fromSubject = await RunAsync(SubjectId, [ViewerId]);

        Assert.That(
            fromSubject.Shared.Single().GuildIds,
            Is.EquivalentTo(fromViewer.Shared.Single().GuildIds));
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AUserWithNoSharedGuilds_IsOmittedRatherThanReturnedEmpty()
    {
        var response = await RunAsync(ViewerId, [StrangerId]);

        Assert.That(response.Shared, Is.Empty);
    }

    [Test]
    public async Task Handle_DuplicateIdsInTheRequest_ProduceOneEntry()
    {
        var response = await RunAsync(ViewerId, [SubjectId, SubjectId]);

        Assert.That(response.Shared, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Handle_ASubjectInNoGuildsAtAll_AnswersNothing()
    {
        var response = await RunAsync("user-nobody", [SubjectId]);

        Assert.That(response.Shared, Is.Empty);
    }

    // ── Negative: this must not become a roster ───────────────────────────────

    [Test]
    public async Task Handle_AskingAboutYourself_DoesNotReturnYourOwnGuildList()
    {
        // The self-pair intersection is the complete membership of that user. The caller is a
        // service, not necessarily that person, so this is the enumeration hole and it stays shut.
        var response = await RunAsync(ViewerId, [ViewerId]);

        Assert.That(response.Shared, Is.Empty);
    }

    [Test]
    public async Task Handle_ASelfPairMixedIntoARealBatch_DropsOnlyTheSelfPair()
    {
        var response = await RunAsync(ViewerId, [ViewerId, SubjectId]);

        Assert.Multiple(() =>
        {
            Assert.That(response.Shared.Select(s => s.OtherUserId), Is.EquivalentTo(new[] { SubjectId }));
            Assert.That(response.Shared.Single().GuildIds, Does.Not.Contain(ViewerOnlyGuild));
        });
    }

    [Test]
    public async Task Handle_NeverRevealsAGuildTheSubjectIsNotIn()
    {
        // The stranger and the subject share SubjectOnlyGuild. The viewer is in neither side of
        // that fact and must not learn it by asking about both.
        var response = await RunAsync(ViewerId, [SubjectId, StrangerId]);

        var everyGuildReturned = response.Shared.SelectMany(s => s.GuildIds).ToList();
        Assert.That(everyGuildReturned, Does.Not.Contain(SubjectOnlyGuild));
    }

    [Test]
    public async Task Handle_WithNoUserId_AnswersNothing()
    {
        var response = await RunAsync("", [SubjectId]);

        Assert.That(response.Shared, Is.Empty);
    }

    [Test]
    public async Task Handle_WithNoOtherUserIds_AnswersNothing()
    {
        var response = await RunAsync(ViewerId, []);

        Assert.That(response.Shared, Is.Empty);
    }

    // ── Negative: blocking ────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ABlockedPair_SharesNothing()
    {
        var response = await RunAsync(ViewerId, [SubjectId], (ViewerId, SubjectId));

        Assert.That(response.Shared, Is.Empty);
    }

    [Test]
    public async Task Handle_ABlockedPair_TheOtherDirection_AlsoSharesNothing()
    {
        var response = await RunAsync(ViewerId, [SubjectId], (SubjectId, ViewerId));

        Assert.That(response.Shared, Is.Empty);
    }

    [Test]
    public async Task Handle_ABlockedPairInABatch_LeavesTheRestOfTheBatchIntact()
    {
        AddMember(ViewerOnlyGuild, StrangerId);
        await _context.SaveChangesAsync();

        var response = await RunAsync(ViewerId, [SubjectId, StrangerId], (ViewerId, SubjectId));

        Assert.That(response.Shared.Select(s => s.OtherUserId), Is.EquivalentTo(new[] { StrangerId }));
    }

    [Test]
    public async Task Handle_WhenSocialIsUnreachable_SharesNothingWithAnyone()
    {
        // Fail closed. A ServerMembers friend request refuses and mutualServers comes back empty,
        // which is exactly what Social's NoSharedGuildResolver did unconditionally before.
        var response = await GetSharedGuildsHandler.Handle(
            new GetSharedGuildsRequest { UserId = ViewerId, OtherUserIds = [SubjectId] },
            _context,
            PrivacyTestFactory.UnreachableBlocks(_bus, _cache));

        Assert.That(response.Shared, Is.Empty);
    }
}
