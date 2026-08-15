using Billing.Application.Credit;
using Identity.Contracts.Bus.Events;

namespace Billing.Application.Bus.Consumers;

/// <summary>Gives the fraud void a trigger (monetization.md section 8.6).</summary>
public class UserModerationStatusChangedHandler
{
    /// <summary>Recorded as the actor when the event carries none.</summary>
    private const string FallbackActor = "system:moderation";

    public static async Task Handle(
        UserModerationStatusChangedEvent message,
        CreditLedgerService ledger,
        ILogger<UserModerationStatusChangedHandler> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(ledger);

        if (!message.Banned) return;

        try
        {
            var result = await ledger.VoidForFraudAsync(
                message.UserId,
                Reason(message),
                string.IsNullOrWhiteSpace(message.ActorUserId) ? FallbackActor : message.ActorUserId,
                cancellationToken);

            if (result.Entries.Count == 0)
            {
                logger.LogInformation(
                    "Ban of {UserId} voided no credit - the wallet was already empty or this event is a replay",
                    message.UserId);

                return;
            }

            logger.LogWarning(
                "Ban of {UserId} by {ActorId} voided {Lots} credit lot(s). Any Stripe subscription this "
                + "account pays for is still live and still billing - cancel it by hand if the ban is final",
                message.UserId, message.ActorUserId, result.Entries.Count);
        }
        catch (CreditRefusedException refusal)
        {
            // Not rethrown.
            logger.LogError(
                "Could not void credit for banned account {UserId}: {Code} {Message}",
                message.UserId, refusal.Code, refusal.Message);
        }
    }

    /// <summary>The ledger requires a reason and puts it on every reversal it writes, so an event with
    /// none still has to say why the balance went to zero.</summary>
    private static string Reason(UserModerationStatusChangedEvent message) =>
        string.IsNullOrWhiteSpace(message.Reason)
            ? "Account banned; outstanding credit voided (monetization.md section 8.6)."
            : $"Account banned; outstanding credit voided. {message.Reason.Trim()}";
}
