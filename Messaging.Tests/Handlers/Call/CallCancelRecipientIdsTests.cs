using Messaging.Application.Handler.Call;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;

// Namespace deliberately matches CallEventHandlersTests rather than the folder: a
// Messaging.Tests.Handlers.Call namespace shadows the Call entity for every file in this folder.
namespace Messaging.Tests.Handlers;

/// <summary>
/// Which users are told to stop ringing when a call is answered or declined. Pure functions for the
/// same reason CallAcceptedCancelRecipientsTests is: the handlers themselves reach CallPushService,
/// whose static ApnsClient touches real Apple credentials.
/// </summary>
[TestFixture]
public class CallCancelRecipientIdsTests
{
    private const string Caller = "user-caller";
    private const string Callee = "user-callee";
    private const string OtherInvitee = "user-invitee";

    private static Call OneToOne(CallStatus calleeStatus) => new()
    {
        Id = "call-1",
        ConversationId = "conv-1",
        CreatorId = Caller,
        Participants =
        [
            new CallParticipant { UserId = Caller, Status = CallStatus.Connected },
            new CallParticipant { UserId = Callee, Status = calleeStatus },
        ],
    };

    [Test]
    public void Accept_OneToOne_TellsOnlyTheAccepter_NotTheCaller()
    {
        // The caller is in the call. A cancel push there makes iOS report and immediately end a
        // phantom CallKit call on top of the live one - which is what the old "everyone but the
        // accepter" rule did on every single 1:1 answer.
        var ids = CallAcceptedHandler.CancelRecipientIds(OneToOne(CallStatus.Connected), Callee);

        Assert.That(ids, Is.EquivalentTo(new[] { Callee }));
    }

    [Test]
    public void Accept_GroupCall_TellsTheAccepterAndTheStillRingingInvitees()
    {
        var call = OneToOne(CallStatus.Connected);
        call.Participants.Add(new CallParticipant { UserId = OtherInvitee, Status = CallStatus.Pending });

        var ids = CallAcceptedHandler.CancelRecipientIds(call, Callee);

        Assert.That(ids, Is.EquivalentTo(new[] { Callee, OtherInvitee }));
    }

    [Test]
    public void Accept_CreatorConnecting_IsNeverToldToCancel()
    {
        // The creator's participant row is still Pending until their Cloudflare session lands, so
        // "still Pending" alone is not enough to keep them out.
        var call = OneToOne(CallStatus.Connected);
        call.Participants.First(p => p.UserId == Caller).Status = CallStatus.Pending;

        var ids = CallAcceptedHandler.CancelRecipientIds(call, Callee);

        Assert.That(ids, Does.Not.Contain(Caller));
    }

    [Test]
    public void Decline_TellsTheDeclinersOwnOtherDevices()
    {
        var ids = CallDeclinedHandler.CancelRecipientIds(OneToOne(CallStatus.Rejected), Callee);

        Assert.That(ids, Is.EquivalentTo(new[] { Callee }));
    }

    [Test]
    public void Decline_DoesNotTellTheCaller()
    {
        var call = OneToOne(CallStatus.Rejected);
        call.Participants.Add(new CallParticipant { UserId = OtherInvitee, Status = CallStatus.Pending });

        var ids = CallDeclinedHandler.CancelRecipientIds(call, Callee);

        Assert.That(ids, Is.EquivalentTo(new[] { Callee, OtherInvitee }));
    }
}
