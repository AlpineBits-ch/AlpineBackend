using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services;

/// <summary>
/// Expires pending invites once they pass <see cref="PlayerInvite.Timeout"/> (2 minutes) and lets the
/// initiator know their invite went stale. Runs on a short tick so timeouts land close to the deadline.
/// </summary>
public sealed class InviteTimeoutService(
    IServiceScopeFactory scopeFactory,
    IBridgeClient bridgeClient,
    ILogger<InviteTimeoutService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ExpireStaleInvitesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Invite timeout tick failed");
            }
        }
    }

    private async Task ExpireStaleInvitesAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - PlayerInvite.Timeout;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var stale = await db.PlayerInvites
            .Include(r => r.SenderPlayer)
            .Include(r => r.ReceiverPlayer)
            .Where(r => r.Status == PlayerInviteStatus.Pending && r.CreatedAt <= cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var invite in stale)
        {
            invite.Expire();
        }

        await db.SaveChangesAsync(ct);

        foreach (var invite in stale)
        {
            var receiverLabel = string.IsNullOrWhiteSpace(invite.ReceiverPlayer.InGameName)
                ? invite.ReceiverPlayer.FriendlyId
                : invite.ReceiverPlayer.InGameName;

            try
            {
                await bridgeClient.DmAsync(
                    text: $"Your invite to {receiverLabel} timed out after 2 minutes without a response.",
                    steam: invite.SenderPlayer.SteamId,
                    sender: "VENTA.GG",
                    mode: ChatMode.Spatial);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to notify initiator {SenderId} about timed-out invite {InviteId}",
                    invite.SenderPlayerId, invite.Id);
            }
        }
    }
}
