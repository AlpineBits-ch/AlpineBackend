using Echo.Voice.Testing;
using Echo.Voice.Rooms;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;

namespace Messaging.Tests.Domain;

/// <summary>
/// Covers Call's business logic directly (no infrastructure involved - this is pure aggregate
/// behavior): Accept/ConnectDevice's device-takeover detection, Decline's stale-device-dismiss vs.
/// real-decline branches (including the "everyone but the creator declined" group-call end
/// condition), Leave's alone-timeout/all-left transitions, End/Timeout/EndIfStillAlone.
/// </summary>
[TestFixture]
public class CallTests
{
    private static CallParticipant Participant(string userId, CallStatus status = CallStatus.Pending, string? activeDeviceId = null) => new()
    {
        UserId = userId,
        Status = status,
        ActiveDeviceId = activeDeviceId,
    };

    private static Call MakeCall(string creatorId, params CallParticipant[] participants) => new()
    {
        Id = "call-1",
        ConversationId = "conv-1",
        CreatorId = creatorId,
        Participants = participants.ToList(),
    };

    // ══════════════════════════════════════════════════════════════════════════ MarkCreated
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void MarkCreated_AddsCallCreatedEvent()
    {
        var call = MakeCall("user-1");

        call.MarkCreated();

        var evt = call.GetDomainEvents().Single();
        Assert.That(evt, Is.InstanceOf<CallCreated>());
    }

    // ══════════════════════════════════════════════════════════════════════════ IsParticipant /
    // IsCreator ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void IsParticipant_And_IsCreator_ReflectMembership()
    {
        var call = MakeCall("user-1", Participant("user-1"), Participant("user-2"));

        Assert.Multiple(() =>
        {
            Assert.That(call.IsParticipant("user-1"), Is.True);
            Assert.That(call.IsParticipant("user-3"), Is.False);
            Assert.That(call.IsCreator("user-1"), Is.True);
            Assert.That(call.IsCreator("user-2"), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Accept /
    // ConnectDevice ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Accept_NonParticipant_IsNoOp()
    {
        var call = MakeCall("user-1", Participant("user-1"));

        call.Accept("ghost", "device-1");

        Assert.That(call.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Accept_FirstTime_ConnectsDeviceAndRaisesCallAccepted()
    {
        var call = MakeCall("user-1", Participant("user-1"), Participant("user-2"));

        call.Accept("user-2", "device-1");

        var participant = call.Participants.Single(p => p.UserId == "user-2");
        Assert.Multiple(() =>
        {
            Assert.That(participant.Status, Is.EqualTo(CallStatus.Connected));
            Assert.That(participant.ActiveDeviceId, Is.EqualTo("device-1"));
            Assert.That(call.Status, Is.EqualTo(CallStatus.Connected));
            Assert.That(call.GetDomainEvents().OfType<CallAccepted>().Single().UserId, Is.EqualTo("user-2"));
        });
    }

    [Test]
    public void Accept_NewParticipantConnecting_ClearsAloneSince()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, "device-1"), Participant("user-2"));
        call.AloneSince = DateTime.UtcNow;

        call.Accept("user-2", "device-2");

        Assert.That(call.AloneSince, Is.Null);
    }

    [Test]
    public void Accept_SameUserFromSecondDevice_RaisesDeviceTakeover_AndClearsOldSession()
    {
        var call = MakeCall("user-1",
            Participant("user-1", CallStatus.Connected, activeDeviceId: "device-1"),
            Participant("user-2"));

        // The superseded device's media handles are read from the voice room by the caller and
        // handed in.
        call.Accept("user-1", "device-2", oldCfSessionId: "cf-old", oldAudioTrackName: "track-old");

        var participant = call.Participants.Single(p => p.UserId == "user-1");
        var takeover = call.GetDomainEvents().OfType<CallDeviceTakeover>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(participant.ActiveDeviceId, Is.EqualTo("device-2"));
            Assert.That(takeover.OldDeviceId, Is.EqualTo("device-1"));
            Assert.That(takeover.NewDeviceId, Is.EqualTo("device-2"));
            Assert.That(takeover.OldCfSessionId, Is.EqualTo("cf-old"),
                "the handler needs this to close the superseded session");
            Assert.That(takeover.OldAudioTrackName, Is.EqualTo("track-old"));
        });
    }

    [Test]
    public void Accept_SameUserSameDevice_IsIdempotent_NoTakeoverEvent()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, activeDeviceId: "device-1"));

        call.Accept("user-1", "device-1");

        Assert.That(call.GetDomainEvents().OfType<CallDeviceTakeover>(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Decline
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Decline_NonParticipant_IsNoOp()
    {
        var call = MakeCall("user-1", Participant("user-1"));

        call.Decline("ghost", "device-1");

        Assert.That(call.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Decline_ParticipantAlreadyConnectedElsewhere_RaisesDeviceDismissed_CallUnaffected()
    {
        var call = MakeCall("user-1", Participant("user-1"), Participant("user-2", CallStatus.Connected, "device-1"));

        call.Decline("user-2", "device-2");

        var evt = call.GetDomainEvents().Single();
        Assert.Multiple(() =>
        {
            Assert.That(evt, Is.InstanceOf<CallDeviceDismissed>());
            Assert.That(((CallDeviceDismissed)evt).DeviceId, Is.EqualTo("device-2"));
            Assert.That(call.Participants.Single(p => p.UserId == "user-2").Status, Is.EqualTo(CallStatus.Connected),
                "The already-connected participant's status must be untouched by a stale decline");
        });
    }

    [Test]
    public void Decline_OneOnOneCall_RejectsAndEndsCall()
    {
        var call = MakeCall("user-1", Participant("user-1"), Participant("user-2"));

        call.Decline("user-2", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Rejected));
            Assert.That(call.Participants.Single(p => p.UserId == "user-2").Status, Is.EqualTo(CallStatus.Rejected));
            Assert.That(call.GetDomainEvents().OfType<CallDeclined>().ToList(), Has.Count.EqualTo(1));
            Assert.That(call.GetDomainEvents().OfType<CallEnded>().Single().Reason, Is.EqualTo(CallEndReason.Declined));
        });
    }

    [Test]
    public void Decline_CarriesTheDecliningDevice_OnTheEvent()
    {
        // The decliner's other devices are still ringing and get a cancel push, so that push has to
        // name the device that acted - it is what lets the declining device recognise and ignore
        // its own copy (CallPushPayload.ExcludeDeviceId).
        var call = MakeCall("user-1", Participant("user-1"), Participant("user-2"), Participant("user-3"));

        call.Decline("user-2", "phone-2");

        Assert.That(call.GetDomainEvents().OfType<CallDeclined>().Single().DeviceId, Is.EqualTo("phone-2"));
    }

    [Test]
    public void Decline_GroupCall_OneOfMultipleDeclines_CallStaysAlive()
    {
        var call = MakeCall("user-1", Participant("user-1"), Participant("user-2"), Participant("user-3"));

        call.Decline("user-2", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Pending), "Call must stay pending while user-3 hasn't responded yet");
            Assert.That(call.GetDomainEvents().OfType<CallEnded>(), Is.Empty);
        });
    }

    [Test]
    public void Decline_GroupCall_EveryoneButCreatorDeclines_EndsCall()
    {
        var call = MakeCall("user-1", Participant("user-1"), Participant("user-2"), Participant("user-3"));

        call.Decline("user-2", "device-1");
        call.Decline("user-3", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Rejected));
            Assert.That(call.GetDomainEvents().OfType<CallEnded>().Single().Reason, Is.EqualTo(CallEndReason.Declined));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Leave
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Leave_NonParticipant_IsNoOp()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, "device-1"));

        call.Leave("ghost", "device-1");

        Assert.That(call.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Leave_ParticipantNotConnected_IsNoOp()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Pending));

        call.Leave("user-1", "device-1");

        Assert.That(call.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Leave_DeviceMismatch_IsNoOp_StaleSignalFromSupersededDevice()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, activeDeviceId: "device-1"));

        call.Leave("user-1", "device-2");

        Assert.That(call.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void Leave_LastConnectedParticipant_CompletesCall()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, activeDeviceId: "device-1"));

        call.Leave("user-1", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Completed));
            Assert.That(call.AloneSince, Is.Null);
            Assert.That(call.GetDomainEvents().OfType<CallParticipantLeft>().ToList(), Has.Count.EqualTo(1));
            Assert.That(call.GetDomainEvents().OfType<CallEnded>().Single().Reason, Is.EqualTo(CallEndReason.AllParticipantsLeft));
        });
    }

    [Test]
    public void Leave_DropsToOneConnectedParticipant_StartsAloneGracePeriod()
    {
        var call = MakeCall("user-1",
            Participant("user-1", CallStatus.Connected, activeDeviceId: "device-1"),
            Participant("user-2", CallStatus.Connected, activeDeviceId: "device-2"));

        call.Leave("user-1", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(call.AloneSince, Is.Not.Null);
            Assert.That(call.GetDomainEvents().OfType<CallWentAlone>().Single().UserId, Is.EqualTo("user-2"));
            Assert.That(call.GetDomainEvents().OfType<CallEnded>(), Is.Empty, "Call must not end immediately - the survivor gets a grace period");
        });
    }

    [Test]
    public void Leave_MultipleStillConnected_NoAloneOrEndEvent()
    {
        var call = MakeCall("user-1",
            Participant("user-1", CallStatus.Connected, activeDeviceId: "device-1"),
            Participant("user-2", CallStatus.Connected, activeDeviceId: "device-2"),
            Participant("user-3", CallStatus.Connected, activeDeviceId: "device-3"));

        call.Leave("user-1", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(call.GetDomainEvents().OfType<CallWentAlone>(), Is.Empty);
            Assert.That(call.GetDomainEvents().OfType<CallEnded>(), Is.Empty);
            Assert.That(call.Status, Is.Not.EqualTo(CallStatus.Completed));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ End
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void End_DefaultReason_CompletesCall_RaisesUserEndedEvent()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, "device-1"));
        call.AloneSince = DateTime.UtcNow;

        call.End();

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Completed));
            Assert.That(call.AloneSince, Is.Null);
            Assert.That(call.GetDomainEvents().OfType<CallEnded>().Single().Reason, Is.EqualTo(CallEndReason.UserEnded));
        });
    }

    [Test]
    public void End_ExplicitReason_IsCarriedThrough()
    {
        var call = MakeCall("user-1");

        call.End(CallEndReason.AloneTimeout);

        Assert.That(call.GetDomainEvents().OfType<CallEnded>().Single().Reason, Is.EqualTo(CallEndReason.AloneTimeout));
    }

    // ══════════════════════════════════════════════════════════════════════════ Timeout
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Timeout_StillPending_RejectsAndEndsCall()
    {
        var call = MakeCall("user-1");
        call.Status = CallStatus.Pending;

        call.Timeout();

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Rejected));
            Assert.That(call.GetDomainEvents().OfType<CallEnded>().Single().Reason, Is.EqualTo(CallEndReason.Declined));
        });
    }

    [Test]
    public void Timeout_AlreadyAccepted_IsNoOp()
    {
        var call = MakeCall("user-1");
        call.Status = CallStatus.Connected;

        call.Timeout();

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Connected));
            Assert.That(call.GetDomainEvents(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ EndIfStillAlone
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void EndIfStillAlone_AloneSinceNoLongerMatches_IsNoOp()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, "device-1"));
        var original = DateTime.UtcNow.AddMinutes(-10);
        call.AloneSince = original;

        call.EndIfStillAlone(original.AddSeconds(1));

        Assert.That(call.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void EndIfStillAlone_NotExactlyOneConnected_IsNoOp()
    {
        var call = MakeCall("user-1",
            Participant("user-1", CallStatus.Connected, "device-1"),
            Participant("user-2", CallStatus.Connected, "device-2"));
        var aloneSince = DateTime.UtcNow;
        call.AloneSince = aloneSince;

        call.EndIfStillAlone(aloneSince);

        Assert.That(call.GetDomainEvents(), Is.Empty);
    }

    [Test]
    public void EndIfStillAlone_MatchesAndStillAlone_EndsCallWithAloneTimeoutReason()
    {
        var call = MakeCall("user-1", Participant("user-1", CallStatus.Connected, "device-1"));
        var aloneSince = DateTime.UtcNow;
        call.AloneSince = aloneSince;

        call.EndIfStillAlone(aloneSince);

        Assert.Multiple(() =>
        {
            Assert.That(call.Status, Is.EqualTo(CallStatus.Completed));
            Assert.That(call.GetDomainEvents().OfType<CallEnded>().Single().Reason, Is.EqualTo(CallEndReason.AloneTimeout));
        });
    }
}
