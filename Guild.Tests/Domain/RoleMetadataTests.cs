using Guild.Domain.Aggregates;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>The role-model rules that live on the aggregate rather than in an endpoint: the pinned
/// @everyone name, the icon/emoji exclusivity, and what a managed role permits. Each is here
/// because there is more than one write path (the role endpoint, the bot sync handler, the Discord
/// importer, template instantiation), and a rule enforced in one of them is not a rule.</summary>
[TestFixture]
public class RoleMetadataTests
{
    private const string GuildId = "guild-1";
    private const string MemberId = "member-1";

    private static Role OrdinaryRole() => Role.Create(new CreateRoleParams
    {
        Name = "moderators", GuildId = GuildId,
    });

    // ── R29: the @everyone name is pinned ─────────────────────────────────────

    [Test]
    public void Rename_OnTheEveryoneRole_Throws()
    {
        var everyone = Role.CreateEveryoneRole(GuildId, MemberId);

        var ex = Assert.Throws<InvalidOperationException>(() => everyone.Rename("General"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("@everyone"));
            Assert.That(everyone.Name, Is.EqualTo(Role.EveryoneRoleName),
                "the name must be unchanged, not merely reported as unchangeable");
        });
    }

    [Test]
    public void Rename_OnTheEveryoneRole_ThrowsEvenForTheSameName()
    {
        // A no-op rename is still a rename attempt.
        var everyone = Role.CreateEveryoneRole(GuildId, MemberId);

        Assert.Throws<InvalidOperationException>(() => everyone.Rename(Role.EveryoneRoleName));
    }

    [Test]
    public void Rename_OnAnOrdinaryRole_Succeeds()
    {
        var role = OrdinaryRole();

        role.Rename("staff");

        Assert.That(role.Name, Is.EqualTo("staff"));
    }

    [Test]
    public void CreateEveryoneRole_UsesThePinnedName()
    {
        Assert.That(Role.CreateEveryoneRole(GuildId, MemberId).Name, Is.EqualTo(Role.EveryoneRoleName));
    }

    // ── R20: icon and unicode emoji are mutually exclusive ────────────────────

    [Test]
    public void SetBadge_WithAnIconOnly_KeepsTheIcon()
    {
        var role = OrdinaryRole();

        role.SetBadge("https://cdn.example/role.png", null);

        Assert.Multiple(() =>
        {
            Assert.That(role.IconUrl, Is.EqualTo("https://cdn.example/role.png"));
            Assert.That(role.UnicodeEmoji, Is.Null);
        });
    }

    [Test]
    public void SetBadge_WithAnEmojiOnly_KeepsTheEmoji()
    {
        var role = OrdinaryRole();

        role.SetBadge(null, "⭐");

        Assert.Multiple(() =>
        {
            Assert.That(role.UnicodeEmoji, Is.EqualTo("⭐"));
            Assert.That(role.IconUrl, Is.Null);
        });
    }

    [Test]
    public void SetBadge_WithBoth_Throws()
    {
        var role = OrdinaryRole();

        Assert.Throws<InvalidOperationException>(
            () => role.SetBadge("https://cdn.example/role.png", "⭐"));
    }

    [Test]
    public void SetBadge_ReplacingAnIconWithAnEmoji_ClearsTheIcon()
    {
        // The exclusivity has to hold across edits, not just on the first write - otherwise a role
        // that was given an icon and later an emoji ends up carrying both.
        var role = OrdinaryRole();
        role.SetBadge("https://cdn.example/role.png", null);

        role.SetBadge(null, "⭐");

        Assert.Multiple(() =>
        {
            Assert.That(role.IconUrl, Is.Null);
            Assert.That(role.UnicodeEmoji, Is.EqualTo("⭐"));
        });
    }

    [Test]
    public void SetBadge_WithNeither_ClearsBoth()
    {
        var role = OrdinaryRole();
        role.SetBadge(null, "⭐");

        role.SetBadge(null, null);

        Assert.Multiple(() =>
        {
            Assert.That(role.IconUrl, Is.Null);
            Assert.That(role.UnicodeEmoji, Is.Null);
        });
    }

    [Test]
    public void SetBadge_TreatsWhitespaceAsAbsent()
    {
        // A client that clears a text field usually sends "" rather than omitting it.
        var role = OrdinaryRole();

        Assert.DoesNotThrow(() => role.SetBadge("   ", "⭐"));
        Assert.Multiple(() =>
        {
            Assert.That(role.IconUrl, Is.Null);
            Assert.That(role.UnicodeEmoji, Is.EqualTo("⭐"));
        });
    }

    [Test]
    public void Create_RoutesTheBadgeThroughTheGuard()
    {
        Assert.Throws<InvalidOperationException>(() => Role.Create(new CreateRoleParams
        {
            Name = "vip", GuildId = GuildId,
            IconUrl = "https://cdn.example/role.png", UnicodeEmoji = "⭐",
        }));
    }

    // ── R18/R19: hoist and mentionable defaults ───────────────────────────────

    [Test]
    public void ANewRole_IsMentionableAndNotHoisted()
    {
        var role = OrdinaryRole();

        Assert.Multiple(() =>
        {
            Assert.That(role.Mentionable, Is.True, "Discord's default, and the one that keeps role pings working");
            Assert.That(role.Hoist, Is.False, "grouping in the member list is opt-in");
        });
    }

    [Test]
    public void TheEveryoneRole_IsAlsoMentionableByDefault()
    {
        // Not a contradiction with MentionEveryone being withheld: this flag says the role *may* be
        // addressed, and MentionEveryone says who may address it. Both must hold.
        Assert.That(Role.CreateEveryoneRole(GuildId, MemberId).Mentionable, Is.True);
    }

    // ── R22: role tags ────────────────────────────────────────────────────────

    [Test]
    public void ANewRole_IsNotManagedAndCarriesNoTags()
    {
        var role = OrdinaryRole();

        Assert.Multiple(() =>
        {
            Assert.That(role.IsManaged, Is.False);
            Assert.That(role.BotUserId, Is.Null);
            Assert.That(role.IntegrationId, Is.Null);
            Assert.That(role.IsEditableByHumans, Is.True);
        });
    }

    [Test]
    public void AManagedRole_IsNotEditableByHumans()
    {
        var role = OrdinaryRole();

        role.IsManaged = true;
        role.BotUserId = "user_bot_1";

        Assert.Multiple(() =>
        {
            Assert.That(role.IsEditableByHumans, Is.False);
            Assert.That(role.BotUserId, Is.EqualTo("user_bot_1"));
        });
    }

    [Test]
    public void AManagedRole_CanStillBeRenamedByTheAggregate()
    {
        // IsEditableByHumans is the endpoints' rule, deliberately not enforced down here: the
        // integration that owns the role has to be able to rename it when its own name changes,
        // and it goes through the same aggregate.
        var role = OrdinaryRole();
        role.IsManaged = true;

        Assert.DoesNotThrow(() => role.Rename("Bot Role v2"));
        Assert.That(role.Name, Is.EqualTo("Bot Role v2"));
    }

    [Test]
    public void AManagedEveryoneRole_IsStillUnrenameable()
    {
        // The two rules compose the way you would expect rather than one overriding the other.
        var everyone = Role.CreateEveryoneRole(GuildId, MemberId);
        everyone.IsManaged = true;

        Assert.Throws<InvalidOperationException>(() => everyone.Rename("General"));
    }
}
