using Isle.Domain.Aggregates;

namespace Isle.Tests.Tests.Domain;

[TestFixture]
public class PlayerInviteTests
{
    [Test]
    public void Create_SetsSenderAndReceiverAndDefaultsToPending()
    {
        var invite = PlayerInvite.Create("player-sender", "player-receiver");

        Assert.That(invite.SenderPlayerId, Is.EqualTo("player-sender"));
        Assert.That(invite.ReceiverPlayerId, Is.EqualTo("player-receiver"));
        Assert.That(invite.Status, Is.EqualTo(PlayerInviteStatus.Pending));
    }

    [Test]
    public void Create_GeneratesANonEmptyId()
    {
        var invite = PlayerInvite.Create("s", "r");

        Assert.That(invite.Id, Is.Not.Null.And.Not.Empty);
        Assert.That(invite.Id, Does.StartWith(PlayerInvite.Prefix));
    }

    [Test]
    public void Accept_SetsStatusToAccepted()
    {
        var invite = PlayerInvite.Create("s", "r");

        invite.Accept();

        Assert.That(invite.Status, Is.EqualTo(PlayerInviteStatus.Accepted));
    }

    [Test]
    public void Reject_SetsStatusToRejected()
    {
        var invite = PlayerInvite.Create("s", "r");

        invite.Reject();

        Assert.That(invite.Status, Is.EqualTo(PlayerInviteStatus.Rejected));
    }

    [Test]
    public void Expire_SetsStatusToExpired()
    {
        var invite = PlayerInvite.Create("s", "r");

        invite.Expire();

        Assert.That(invite.Status, Is.EqualTo(PlayerInviteStatus.Expired));
    }

    [Test]
    public void Accept_AfterReject_OverwritesStatus()
    {
        // No state-machine guard exists today - document the current (permissive) behaviour.
        var invite = PlayerInvite.Create("s", "r");
        invite.Reject();

        invite.Accept();

        Assert.That(invite.Status, Is.EqualTo(PlayerInviteStatus.Accepted));
    }
}
