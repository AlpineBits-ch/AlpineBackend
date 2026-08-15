using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>
/// <see cref="GuildInvite.EffectiveState"/> and the slug grammar behind vanity URLs - the two pure
/// functions this round's behaviour hangs off, tested away from the endpoints that call them.
/// </summary>
[TestFixture]
public class GuildInviteStateTests
{
    private static GuildInvite Invite(DateTimeOffset? expiresAt = null, int? maxUses = null, int useCount = 0,
        InviteState state = InviteState.Active)
    {
        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = "guild-1", Type = InviteType.Permanent, ExpiresAt = expiresAt, MaxUses = maxUses,
        });
        invite.UseCount = useCount;
        invite.State = state;
        return invite;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void A_fresh_invite_is_active() =>
        Assert.That(Invite().EffectiveState(Now), Is.EqualTo(InviteState.Active));

    [Test]
    public void An_invite_past_its_expiry_is_expired() =>
        Assert.That(Invite(expiresAt: Now.AddSeconds(-1)).EffectiveState(Now), Is.EqualTo(InviteState.Expired));

    /// <summary>Boundary: the comparison is <c>&lt;=</c>, so an invite expiring exactly now is
    /// already gone. Erring towards expired is the safe direction for a join credential.</summary>
    [Test]
    public void An_invite_expiring_exactly_now_is_expired() =>
        Assert.That(Invite(expiresAt: Now).EffectiveState(Now), Is.EqualTo(InviteState.Expired));

    [Test]
    public void An_invite_expiring_in_a_second_is_still_active() =>
        Assert.That(Invite(expiresAt: Now.AddSeconds(1)).EffectiveState(Now), Is.EqualTo(InviteState.Active));

    [Test]
    public void An_invite_with_no_expiry_never_expires_on_its_own() =>
        Assert.That(Invite(expiresAt: null).EffectiveState(Now.AddYears(50)), Is.EqualTo(InviteState.Active));

    [Test]
    public void An_exhausted_invite_is_expired() =>
        Assert.That(Invite(maxUses: 3, useCount: 3).EffectiveState(Now), Is.EqualTo(InviteState.Expired));

    [Test]
    public void An_invite_with_one_use_left_is_active() =>
        Assert.That(Invite(maxUses: 3, useCount: 2).EffectiveState(Now), Is.EqualTo(InviteState.Active));

    [Test]
    public void Null_max_uses_is_unlimited() =>
        Assert.That(Invite(maxUses: null, useCount: 10_000).EffectiveState(Now), Is.EqualTo(InviteState.Active));

    /// <summary>Terminal-by-decision beats anything computed.</summary>
    [Test]
    public void A_stored_expiry_wins_over_a_clock_that_says_otherwise() =>
        Assert.That(Invite(state: InviteState.Expired).EffectiveState(Now), Is.EqualTo(InviteState.Expired));

    [Test]
    public void Revoked_is_reported_as_revoked_not_flattened_to_expired() =>
        Assert.That(Invite(expiresAt: Now.AddSeconds(-1), state: InviteState.Revoked).EffectiveState(Now),
            Is.EqualTo(InviteState.Revoked));

    [Test]
    public void Revoking_stamps_the_time()
    {
        var invite = Invite();
        invite.Revoke(Now);

        Assert.Multiple(() =>
        {
            Assert.That(invite.State, Is.EqualTo(InviteState.Revoked));
            Assert.That(invite.RevokedAt, Is.EqualTo(Now));
            Assert.That(invite.IsRedeemable(Now), Is.False);
        });
    }

    [Test]
    public void Redeemability_is_the_same_predicate_the_read_paths_report()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Invite().IsRedeemable(Now), Is.True);
            Assert.That(Invite(expiresAt: Now.AddDays(-1)).IsRedeemable(Now), Is.False);
            Assert.That(Invite(maxUses: 1, useCount: 1).IsRedeemable(Now), Is.False);
        });
    }
}

[TestFixture]
public class VanitySlugTests
{
    [TestCase("The-FLAT", "the-flat")]
    [TestCase("  spaced  ", "spaced")]
    [TestCase("already-fine", "already-fine")]
    public void Normalize_lowercases_and_trims(string input, string expected) =>
        Assert.That(VanitySlug.Normalize(input), Is.EqualTo(expected));

    [TestCase("abc")]
    [TestCase("the-flat")]
    [TestCase("a1-b2-c3")]
    [TestCase("0123456789012345678901234567890a")]
    public void Valid_slugs_pass(string slug) =>
        Assert.That(VanitySlug.Validate(slug), Is.Null);

    [TestCase("ab", TestName = "Too short")]
    [TestCase("01234567890123456789012345678901a", TestName = "Too long")]
    [TestCase("-lead", TestName = "Leading hyphen")]
    [TestCase("trail-", TestName = "Trailing hyphen")]
    [TestCase("a--b", TestName = "Doubled hyphen")]
    [TestCase("Upper", TestName = "Uppercase reaches Validate only unnormalized")]
    [TestCase("under_score", TestName = "Underscore")]
    [TestCase("with space", TestName = "Space")]
    [TestCase("dots.here", TestName = "Dot")]
    [TestCase("", TestName = "Empty")]
    public void Invalid_slugs_are_refused_with_a_reason(string slug) =>
        Assert.That(VanitySlug.Validate(slug), Is.Not.Null.And.Not.Empty);

    [Test]
    public void Reserved_words_are_refused()
    {
        // The list is about impersonation: a vanity slug is the last segment of a URL somebody is
        // asked to trust.
        Assert.Multiple(() =>
        {
            Assert.That(VanitySlug.Validate("support"), Is.Not.Null);
            Assert.That(VanitySlug.Validate("billing"), Is.Not.Null);
            Assert.That(VanitySlug.Validate("venta"), Is.Not.Null);
            Assert.That(VanitySlug.IsReserved(VanitySlug.Normalize("SUPPORT")), Is.True,
                "casing must not slip one through");
        });
    }
}
