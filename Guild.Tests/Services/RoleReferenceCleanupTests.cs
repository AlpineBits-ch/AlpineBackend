using Guild.Application.Bus.Events.Role;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// R17: the two references to a role that no foreign key covers, and what happens to them when the
/// role is deleted.
/// </summary>
[TestFixture]
public class RoleReferenceCleanupTests
{
    private const string GuildId = "guild-1";
    private const string OtherGuildId = "guild-2";
    private const string RoleId = "role-doomed";
    private const string KeptRoleId = "role-kept";
    private const string MemberId = "member-1";
    private const string ChannelId = "channel-chores";

    private TestGuildContext _context = null!;
    private RoleReferenceCleanupService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _service = new RoleReferenceCleanupService(
            _context, NullLogger<RoleReferenceCleanupService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<GuildOnboardingPromptOption> SeedOptionAsync(
        string guildId, List<string> roleIds, string promptId = "prompt-1", string optionId = "option-1")
    {
        _context.Set<GuildOnboardingPrompt>().Add(new GuildOnboardingPrompt
        {
            Id = promptId, GuildId = guildId, Title = "Pick a team",
            Type = OnboardingPromptType.MultipleChoice,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        var option = new GuildOnboardingPromptOption
        {
            Id = optionId, PromptId = promptId, Title = "Blue", RoleIds = roleIds,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.Set<GuildOnboardingPromptOption>().Add(option);

        await _context.SaveChangesAsync();
        return option;
    }

    private async Task<Chore> SeedChoreAsync(
        string guildId, string? rotationRoleId, string? fixedAssignee = null, string id = "chore-1")
    {
        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChannelId,
            GuildId = guildId,
            Title = "Bins",
            AnchorAt = DateTimeOffset.UtcNow,
            RotationRoleId = rotationRoleId,
            FixedAssigneeUserId = fixedAssignee,
        });
        chore.Id = id;
        _context.Chores.Add(chore);
        await _context.SaveChangesAsync();
        return chore;
    }

    // ══════════════════════════════════════════════════════════════════════════ Onboarding options
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Cleanup_RemovesTheDeletedRoleFromOnboardingGrantLists()
    {
        await SeedOptionAsync(GuildId, [RoleId, KeptRoleId]);

        var result = await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var option = await _context.Set<GuildOnboardingPromptOption>().AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(option.RoleIds, Is.EqualTo(new[] { KeptRoleId }));
            Assert.That(result.OnboardingOptionsUpdated, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Cleanup_LeavesOptionsThatNeverNamedTheRole()
    {
        await SeedOptionAsync(GuildId, [KeptRoleId]);

        var result = await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var option = await _context.Set<GuildOnboardingPromptOption>().AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(option.RoleIds, Is.EqualTo(new[] { KeptRoleId }));
            Assert.That(result.OnboardingOptionsUpdated, Is.Zero);
        });
    }

    [Test]
    public async Task Cleanup_DoesNotTouchAnotherGuildsOptions()
    {
        // Role ids are unique in practice, but the cleanup is scoped by guild on purpose: it walks
        // rows by prompt, and an unscoped walk would be a full-table pass on every role delete.
        await SeedOptionAsync(OtherGuildId, [RoleId], promptId: "prompt-other", optionId: "option-other");

        var result = await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var option = await _context.Set<GuildOnboardingPromptOption>().AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(option.RoleIds, Is.EqualTo(new[] { RoleId }));
            Assert.That(result.OnboardingOptionsUpdated, Is.Zero);
        });
    }

    [Test]
    public async Task Cleanup_RemovesTheGrantRowsRecordingTheRoleWasHandedOut()
    {
        _context.Set<GuildOnboardingGrant>().Add(
            GuildOnboardingGrant.ForRole(GuildId, MemberId, "option-1", RoleId));
        _context.Set<GuildOnboardingGrant>().Add(
            GuildOnboardingGrant.ForRole(GuildId, MemberId, "option-1", KeptRoleId));
        await _context.SaveChangesAsync();

        var result = await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var remaining = await _context.Set<GuildOnboardingGrant>().AsNoTracking().ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Has.Count.EqualTo(1));
            Assert.That(remaining[0].RoleId, Is.EqualTo(KeptRoleId));
            Assert.That(result.OnboardingGrantsRemoved, Is.EqualTo(1));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Chores
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Cleanup_PausesAChoreWhoseOnlyRotationPoolWasTheDeletedRole()
    {
        await SeedChoreAsync(GuildId, RoleId);

        var result = await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var chore = await _context.Chores.AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(chore.RotationRoleId, Is.Null, "the dangling reference is gone");
            Assert.That(chore.IsPaused, Is.True,
                "and the chore says so, rather than staying active and generating nothing");
            Assert.That(result.ChoresDetached, Is.EqualTo(1));
            Assert.That(result.ChoresPaused, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Cleanup_LeavesAChoreRunningWhenItStillHasAFixedAssignee()
    {
        await SeedChoreAsync(GuildId, RoleId, fixedAssignee: "user-7");

        var result = await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var chore = await _context.Chores.AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(chore.RotationRoleId, Is.Null);
            Assert.That(chore.IsPaused, Is.False,
                "losing the rota degrades it to 'always theirs', which is a working chore");
            Assert.That(result.ChoresDetached, Is.EqualTo(1));
            Assert.That(result.ChoresPaused, Is.Zero);
        });
    }

    [Test]
    public async Task Cleanup_LeavesChoresRotatingOverOtherRolesAlone()
    {
        await SeedChoreAsync(GuildId, KeptRoleId);

        var result = await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var chore = await _context.Chores.AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(chore.RotationRoleId, Is.EqualTo(KeptRoleId));
            Assert.That(chore.IsPaused, Is.False);
            Assert.That(result.ChoresDetached, Is.Zero);
        });
    }

    [Test]
    public async Task Cleanup_DoesNotTouchAnotherGuildsChores()
    {
        await SeedChoreAsync(OtherGuildId, RoleId, id: "chore-other");

        await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        var chore = await _context.Chores.AsNoTracking().FirstAsync();

        Assert.That(chore.RotationRoleId, Is.EqualTo(RoleId));
    }

    [Test]
    public async Task Cleanup_IsANoOpWhenNothingReferencedTheRole()
    {
        var result = await _service.CleanupAsync(GuildId, RoleId);

        Assert.That(result, Is.EqualTo(new RoleReferenceCleanup(0, 0, 0, 0)));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The paused chore stops the silent-rotation behaviour end to end
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DetachedChore_NoLongerRotatesOverAnEmptyPoolForever()
    {
        var chore = await SeedChoreAsync(GuildId, RoleId);
        var rotation = new ChoreRotationService(_context);

        // Before: the role is gone, the pool is empty, and generation quietly answers null - the
        // failure mode R17 describes.
        Assert.That(await rotation.StageNextOccurrenceAsync(chore), Is.Null);
        Assert.That(chore.IsPaused, Is.False, "with nothing to say it is broken");

        await _service.CleanupAsync(GuildId, RoleId);
        await _context.SaveChangesAsync();

        Assert.That(chore.IsPaused, Is.True);
        Assert.That(await rotation.StageNextOccurrenceAsync(chore), Is.Null,
            "still generates nothing, but now visibly so");
    }

    // ══════════════════════════════════════════════════════════════════════════ Handler wiring
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handler_RunsTheCleanupForTheDeletedRole()
    {
        await SeedOptionAsync(GuildId, [RoleId]);
        await SeedChoreAsync(GuildId, RoleId);

        await RoleDeletedCleanupHandler.Handle(
            new RoleDeleted { RoleId = RoleId, GuildId = GuildId, UserIds = [] },
            _service,
            _context);
        await _context.SaveChangesAsync();

        var option = await _context.Set<GuildOnboardingPromptOption>().AsNoTracking().FirstAsync();
        var chore = await _context.Chores.AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(option.RoleIds, Is.Empty);
            Assert.That(chore.RotationRoleId, Is.Null);
            Assert.That(chore.IsPaused, Is.True);
        });
    }
}
