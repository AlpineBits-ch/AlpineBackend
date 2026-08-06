using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;

namespace Messaging.Tests.Domain;

/// <summary>Covers the facts <see cref="CallEnded"/> carries.</summary>
[TestFixture]
public class CallEndedFactsTests
{
    private const string Caller = "user-caller";
    private const string Callee = "user-callee";
    private const string Other = "user-other";

    private static Call NewCall(params string[] participantIds) => new()
    {
        Id = "call-1",
        ConversationId = "conv-1",
        CreatorId = Caller,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        UpdatedAt = DateTimeOffset.UtcNow,
        Participants = participantIds.Select(id => new CallParticipant { UserId = id }).ToList(),
    };

    private static CallEnded Ended(Call call) => call.GetDomainEvents().OfType<CallEnded>().Single();

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void End_CarriesTheConversationAndRoster()
    {
        var call = NewCall(Caller, Callee);

        call.End();

        var ended = Ended(call);
        Assert.Multiple(() =>
        {
            Assert.That(ended.CallId, Is.EqualTo("call-1"));
            Assert.That(ended.ConversationId, Is.EqualTo("conv-1"));
            Assert.That(ended.CreatorId, Is.EqualTo(Caller));
            Assert.That(ended.ParticipantIds, Is.EquivalentTo(new[] { Caller, Callee }));
            Assert.That(ended.StartedAt, Is.EqualTo(call.CreatedAt));
        });
    }

    [Test]
    public void RingTimeout_IsNotAnsweredByAnyone()
    {
        var call = NewCall(Caller, Callee);

        call.Timeout();

        Assert.That(Ended(call).Answered, Is.False,
            "nobody picked up - this is the missed call case");
    }

    [Test]
    public void Decline_IsNotAnsweredByAnyone()
    {
        var call = NewCall(Caller, Callee);

        call.Decline(Callee, "device-1");

        Assert.That(Ended(call).Answered, Is.False);
    }

    [Test]
    public void AcceptThenEnd_IsAnswered()
    {
        var call = NewCall(Caller, Callee);
        call.Accept(Callee, "device-1");
        call.ClearDomainEvents();

        call.End();

        Assert.That(Ended(call).Answered, Is.True);
    }

    [Test]
    public void AnsweredThenHungUp_IsStillAnswered()
    {
        // Leaving flips the participant to Left, not back to Pending.
        var call = NewCall(Caller, Callee);
        call.Accept(Callee, "device-1");
        call.Accept(Caller, "device-2");
        call.Leave(Callee, "device-1");
        call.ClearDomainEvents();

        call.End();

        Assert.That(Ended(call).Answered, Is.True);
    }

    [Test]
    public void LastParticipantLeaving_EndsAnsweredCall()
    {
        var call = NewCall(Caller, Callee);
        call.Accept(Callee, "device-1");
        call.Accept(Caller, "device-2");
        call.Leave(Callee, "device-1");
        call.ClearDomainEvents();

        call.Leave(Caller, "device-2");

        Assert.Multiple(() =>
        {
            Assert.That(Ended(call).Reason, Is.EqualTo(CallEndReason.AllParticipantsLeft));
            Assert.That(Ended(call).Answered, Is.True);
        });
    }

    [Test]
    public void CallerConnectingAlone_IsNotAnswered()
    {
        // The caller's own leg connects as soon as they mint a Cloudflare session, long before
        // anyone picks up.
        var call = NewCall(Caller, Callee);
        var caller = call.Participants.First(p => p.UserId == Caller);
        call.ConnectDevice(caller, "device-1");
        call.ClearDomainEvents();

        call.End();

        Assert.That(Ended(call).Answered, Is.False);
    }

    [Test]
    public void GroupCall_IsAnsweredWhenAnyInviteeConnects()
    {
        var call = NewCall(Caller, Callee, Other);
        call.Accept(Other, "device-3");
        call.ClearDomainEvents();

        call.End();

        Assert.That(Ended(call).Answered, Is.True);
    }

    [Test]
    public void CallWithNoConversation_CarriesAnEmptyConversationId()
    {
        var call = NewCall(Caller, Callee);
        call.ConversationId = string.Empty;

        call.End();

        Assert.That(Ended(call).ConversationId, Is.Empty,
            "the handler keys the whole conversation-side branch off this being non-empty");
    }
}
