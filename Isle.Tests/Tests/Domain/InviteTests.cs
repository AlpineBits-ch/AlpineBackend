using Isle.Domain.Entity;

namespace Isle.Tests.Tests.Domain;

[TestFixture]
public class InviteTests
{
    [Test]
    public void Create_CopiesServerAndPlayerIds()
    {
        var invite = Invite.Create(new CreateInviteParams
        {
            ServerId = "server-1",
            SenderPlayerId = "sender-1",
            ReceiverPlayerId = "receiver-1",
        });

        Assert.That(invite.ServerId, Is.EqualTo("server-1"));
        Assert.That(invite.SenderPlayerId, Is.EqualTo("sender-1"));
        Assert.That(invite.ReceiverPlayerId, Is.EqualTo("receiver-1"));
    }

    [Test]
    public void Create_GeneratesANonEmptyIdWithThePrefix()
    {
        var invite = Invite.Create(new CreateInviteParams { ServerId = "s", SenderPlayerId = "a", ReceiverPlayerId = "b" });

        Assert.That(invite.Id, Does.StartWith(Invite.Prefix));
    }

    [Test]
    public void FriendlyId_IsWithinExpectedRange()
    {
        var invite = Invite.Create(new CreateInviteParams { ServerId = "s", SenderPlayerId = "a", ReceiverPlayerId = "b" });

        Assert.That(invite.FriendlyId, Is.InRange(0, 4999));
    }
}
