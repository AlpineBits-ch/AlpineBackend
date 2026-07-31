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
            [Token(Accepter, "desktop-token", AcceptingDevice)], Accepter, AcceptingDevice);

        Assert.That(recipients, Is.Empty);
    }

    [Test]
    public void AccepterUnattributedToken_IsNotToldToCancel()
    {
        // The state of every token immediately after the consolidation migration. Sending here
        // would dismiss the call on the very device that just answered, since the token might be
        // the accepting device's own.
        var recipients = CallAcceptedHandler.CancelRecipients(
            [Token(Accepter, "legacy-token", null)], Accepter, AcceptingDevice);

        Assert.That(recipients, Is.Empty);
    }

    [Test]
    public void AccepterOtherDevice_IsToldToCancel()
    {
        var recipients = CallAcceptedHandler.CancelRecipients(
            [Token(Accepter, "phone-token", "phone-1")], Accepter, AcceptingDevice);

        Assert.That(recipients.Select(t => t.Token), Is.EquivalentTo(new[] { "phone-token" }));
    }

    [Test]
    public void OtherParticipants_KeepEveryToken_AttributedOrNot()
    {
        var recipients = CallAcceptedHandler.CancelRecipients(
        [
            Token("user-2", "their-legacy", null),
            Token("user-2", "their-phone", "phone-9"),
        ], Accepter, AcceptingDevice);

        Assert.That(recipients.Select(t => t.Token), Is.EquivalentTo(new[] { "their-legacy", "their-phone" }));
    }

    [Test]
    public void NoAcceptingDeviceId_StillSpares_TheAccepterEntirely()
    {
        // A pre-update client sends no device id, so the handler passes null and never adds the
        // accepter to the recipient list - but if it ever did, unattributed tokens must not slip
        // through here either.
        var recipients = CallAcceptedHandler.CancelRecipients(
            [Token(Accepter, "legacy-token", null)], Accepter, null);

        Assert.That(recipients, Is.Empty);
    }
}
