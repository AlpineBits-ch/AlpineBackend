using Identity.Application.Services;
using Identity.Contracts.Bus.Commands;
using Identity.Infrastructure.Persistence;

namespace Identity.Application.Consumers;

/// <summary>Renders and sends the three billing mails.</summary>
public class BillingNotificationHandler
{
    public async Task Handle(
        CreditIssuedNotification message,
        MicroserviceContext ctx,
        BillingMailService mail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(mail);

        var recipient = await mail.RecipientAsync(ctx, message.UserId, cancellationToken);
        if (recipient is null) return;

        if (!await mail.TryClaimAsync(
                ctx, message.DedupeKey, message.UserId, nameof(CreditIssuedNotification),
                message.OccurredAt, cancellationToken))
        {
            return;
        }

        await mail.SendCreditIssuedAsync(recipient, message, cancellationToken);
    }

    public async Task Handle(
        EntitlementGrantNotification message,
        MicroserviceContext ctx,
        BillingMailService mail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(mail);

        var recipient = await mail.RecipientAsync(ctx, message.UserId, cancellationToken);
        if (recipient is null) return;

        if (!await mail.TryClaimAsync(
                ctx, message.DedupeKey, message.UserId, nameof(EntitlementGrantNotification),
                message.OccurredAt, cancellationToken))
        {
            return;
        }

        await mail.SendGrantChangedAsync(recipient, message, cancellationToken);
    }

    public async Task Handle(
        PlanUpgradedNotification message,
        MicroserviceContext ctx,
        BillingMailService mail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(mail);

        var recipient = await mail.RecipientAsync(ctx, message.UserId, cancellationToken);
        if (recipient is null) return;

        if (!await mail.TryClaimAsync(
                ctx, message.DedupeKey, message.UserId, nameof(PlanUpgradedNotification),
                message.OccurredAt, cancellationToken))
        {
            return;
        }

        await mail.SendPlanUpgradedAsync(recipient, message, cancellationToken);
    }
}
