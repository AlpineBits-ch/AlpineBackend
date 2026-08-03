using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Handler.Call;

// Namespace deliberately matches CallEventHandlersTests rather than the folder: a
// Messaging.Tests.Handlers.Call namespace shadows the Call entity for every file in this folder.
namespace Messaging.Tests.Handlers;

/// <summary>
/// Who gets the "stop ringing" push after an accept. Exercised as a pure function because
/// CallEventHandlersTests can't drive CallPushService with a non-empty token list (its static
/// ApnsClient touches real Apple credentials).
/// </summary>
[TestFixture]
public class CallAcceptedCancelRecipientsTests
{
    private const string Accepter = "user-1";
    private const string AcceptingDevice = "desktop-1";

    private static PushTokenResponse Token(string userId, string token, string? clientDeviceId) => new()
    {
        UserId = userId,
        Token = token,
        Kind = PushTokenKind.Fcm,
        ClientDeviceId = clientDeviceId,
    };

    [Test]
    public void AcceptingDevice_IsNeverToldToCancel()
    {
        var recipients = CallAcceptedHandler.CancelRecipients(
            [Token(Accepter, "desktop-token", AcceptingDevice)], AcceptingDevice);

        Assert.That(recipients, Is.Empty);
    }

    [Test]
    public void AccepterOtherDevice_IsToldToCancel()
    {
        var recipients = CallAcceptedHandler.CancelRecipients(
            [Token(Accepter, "phone-token", "phone-1")], AcceptingDevice);

        Assert.That(recipients.Select(t => t.Token), Is.EquivalentTo(new[] { "phone-token" }));
    }

    [Test]
    public void AccepterUnattributedToken_IsToldToCancel()
    {
        // The state of every token registered before the device-identity consolidation. These used
        // to be skipped in case one of them was the accepting device's own - which spared the whole
        // account, so a handset holding a legacy token rang on after the call was picked up on the
        // desktop. It gets the push now; the accepting device recognises its own cancel from the
        // payload's excludeDeviceId and drops it there, which no inference here can match.
        var recipients = CallAcceptedHandler.CancelRecipients(
            [Token(Accepter, "legacy-token", null)], AcceptingDevice);

        Assert.That(recipients.Select(t => t.Token), Is.EquivalentTo(new[] { "legacy-token" }));
    }

    [Test]
    public void OtherParticipants_KeepEveryToken_AttributedOrNot()
    {
        var recipients = CallAcceptedHandler.CancelRecipients(
        [
            Token("user-2", "their-legacy", null),
            Token("user-2", "their-phone", "phone-9"),
        ], AcceptingDevice);

        Assert.That(recipients.Select(t => t.Token), Is.EquivalentTo(new[] { "their-legacy", "their-phone" }));
    }

    [Test]
    public void NoAcceptingDeviceId_CancelsEverywhere()
    {
        // A client that sent no device id (or an unregistered one) can't be identified here, so
        // nothing is held back - every ringing device hears about it. This is the case that left a
        // phone ringing indefinitely when the call was answered on a client the server couldn't
        // place.
        var recipients = CallAcceptedHandler.CancelRecipients(
            [Token(Accepter, "legacy-token", null), Token(Accepter, "phone-token", "phone-1")], null);

        Assert.That(recipients.Select(t => t.Token), Is.EquivalentTo(new[] { "legacy-token", "phone-token" }));
    }
}
