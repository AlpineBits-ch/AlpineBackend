using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Events.Player;
using IsleBridge.Sdk.Models;

namespace Isle.Tests.Tests.Domain;

[TestFixture]
public class PlayerAggregateTests
{
    private static Player CreatePlayer(bool isAdmin = false) =>
        Player.Create(new CreatePlayerArgs { SteamId = "steam-1", IsAdmin = isAdmin });

    [Test]
    public void Create_SetsInitialXpTo25000()
    {
        var player = CreatePlayer();

        Assert.That(player.Xp, Is.EqualTo(25000));
    }

    [Test]
    public void Create_CreatesAnAttachedStorage()
    {
        var player = CreatePlayer();

        Assert.That(player.Storage, Is.Not.Null);
    }

    [Test]
    public void Create_RaisesPlayerCreatedDomainEvent()
    {
        var player = CreatePlayer();

        Assert.That(player.GetDomainEvents(), Has.Exactly(1).InstanceOf<PlayerCreated>());
    }

    // ── LinkUserId / UnlinkUserId ────────────────────────────────────────────

    [Test]
    public void LinkUserId_SetsUserIdAndRaisesPlayerUserIdLinked()
    {
        var player = CreatePlayer();
        player.ClearDomainEvents();

        player.LinkUserId("usr_123");

        Assert.That(player.UserId, Is.EqualTo("usr_123"));
        Assert.That(player.GetDomainEvents(), Has.Exactly(1).InstanceOf<PlayerUserIdLinked>(),
            "Linking must raise PlayerUserIdLinked, not PlayerUserIdUnlinked");
        Assert.That(player.GetDomainEvents(), Has.None.InstanceOf<PlayerUserIdUnlinked>());
    }

    [Test]
    public void UnlinkUserId_ClearsUserIdAndRaisesPlayerUserIdUnlinked()
    {
        var player = CreatePlayer();
        player.LinkUserId("usr_123");
        player.ClearDomainEvents();

        player.UnlinkUserId();

        Assert.That(player.UserId, Is.Null);
        Assert.That(player.GetDomainEvents(), Has.Exactly(1).InstanceOf<PlayerUserIdUnlinked>());
    }

    // ── AddXp / TrySpendXp ───────────────────────────────────────────────────

    [Test]
    public void AddXp_PositiveAmount_IncreasesXp()
    {
        var player = CreatePlayer();
        var before = player.Xp;

        player.AddXp(500);

        Assert.That(player.Xp, Is.EqualTo(before + 500));
    }

    [TestCase(0)]
    [TestCase(-100)]
    public void AddXp_NonPositiveAmount_IsIgnored(long amount)
    {
        var player = CreatePlayer();
        var before = player.Xp;

        player.AddXp(amount);

        Assert.That(player.Xp, Is.EqualTo(before));
    }

    [Test]
    public void TrySpendXp_SufficientBalance_DeductsAndReturnsTrue()
    {
        var player = CreatePlayer();
        var before = player.Xp;

        var result = player.TrySpendXp(1000);

        Assert.That(result, Is.True);
        Assert.That(player.Xp, Is.EqualTo(before - 1000));
    }

    [Test]
    public void TrySpendXp_InsufficientBalance_ReturnsFalseAndLeavesBalanceUnchanged()
    {
        var player = CreatePlayer();
        var before = player.Xp;

        var result = player.TrySpendXp(before + 1);

        Assert.That(result, Is.False);
        Assert.That(player.Xp, Is.EqualTo(before));
    }

    [TestCase(0)]
    [TestCase(-50)]
    public void TrySpendXp_NonPositiveAmount_ReturnsFalse(long amount)
    {
        var player = CreatePlayer();

        var result = player.TrySpendXp(amount);

        Assert.That(result, Is.False);
    }

    // ── SetAdmin / UnsetAdmin ─────────────────────────────────────────────────

    [Test]
    public void SetAdmin_SetsIsAdminTrueAndRaisesEvent()
    {
        var player = CreatePlayer();

        player.SetAdmin();

        Assert.That(player.IsAdmin, Is.True);
        Assert.That(player.GetDomainEvents(), Has.Some.InstanceOf<Isle.Domain.Events.Player.PlayerPromotedToAdmin>());
    }

    [Test]
    public void UnsetAdmin_SetsIsAdminFalseAndRaisesEvent()
    {
        var player = CreatePlayer(isAdmin: true);
        player.ClearDomainEvents();

        player.UnsetAdmin();

        Assert.That(player.IsAdmin, Is.False);
        Assert.That(player.GetDomainEvents(), Has.Some.InstanceOf<Isle.Domain.Events.Player.PlayerRemovedFromAdmin>());
    }

    // ── AddSkin ───────────────────────────────────────────────────────────────

    [Test]
    public void AddSkin_AddsSkinAndRaisesSkinCreatedEvent()
    {
        var player = CreatePlayer();
        player.ClearDomainEvents();

        var skinId = player.AddSkin(new CreateSkinParams { PlayerId = player.Id, Customizer = new SkinCustomizer() });

        Assert.That(player.Skins, Has.Count.EqualTo(1));
        Assert.That(player.Skins.Single().Id, Is.EqualTo(skinId));
        Assert.That(player.GetDomainEvents(), Has.Some.InstanceOf<SkinCreatedEvent>());
    }

    // ── FriendlyId round trip ────────────────────────────────────────────────

    [Test]
    public void DecodeFriendlyId_RoundTripsAnEncodedId()
    {
        var player = CreatePlayer();
        player.FriendlyIdSeq = 42;

        var decoded = Player.DecodeFriendlyId(player.FriendlyId);

        Assert.That(decoded, Is.EqualTo(42));
    }

    [Test]
    public void DecodeFriendlyId_ArbitraryString_ReturnsNull()
    {
        var decoded = Player.DecodeFriendlyId("not-a-real-friendly-id!!");

        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void DecodeFriendlyId_NullOrWhitespace_ReturnsNull()
    {
        Assert.That(Player.DecodeFriendlyId(null), Is.Null);
        Assert.That(Player.DecodeFriendlyId("   "), Is.Null);
    }
}
