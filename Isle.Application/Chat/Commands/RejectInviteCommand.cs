using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.Commands;

public class RejectInviteCommand(MicroserviceContext microserviceContext) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var pending = await microserviceContext.PlayerInvites
            .Include(r => r.SenderPlayer)
            .Where(r => r.ReceiverPlayerId == context.PlayerId && r.Status == PlayerInviteStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        if (pending.Count == 0)
        {
            return "You have no pending invites.";
        }

        var identifier = string.Join(' ', context.Arguments).Trim();
        PlayerInvite invite;

        if (identifier.Length == 0)
        {
            if (pending.Count > 1)
            {
                var senders = string.Join(", ", pending.Select(r =>
                    $"{r.SenderPlayer.InGameName ?? r.SenderPlayer.FriendlyId} ({r.SenderPlayer.FriendlyId})"));
                return $"You have {pending.Count} invites. Reject one with !reject friendly id. From: {senders}";
            }

            invite = pending[0];
        }
        else
        {
            var resolution = await PlayerResolver.ResolveAsync(microserviceContext, identifier);
            if (resolution.Outcome == PlayerResolver.ResolveOutcome.Ambiguous)
            {
                return "Multiple players share that in-game name, please use friendly id.";
            }

            var match = resolution.Player is { } sender
                ? pending.FirstOrDefault(r => r.SenderPlayerId == sender.Id)
                : null;
            if (match is null)
            {
                return $"You have no pending invite from \"{identifier}\".";
            }

            invite = match;
        }

        invite.Reject();
        await microserviceContext.SaveChangesAsync();

        return $"Rejected the invite from {invite.SenderPlayer.InGameName ?? invite.SenderPlayer.FriendlyId}.";
    }

    public override string Name { get; } = "reject";
    public override string Description { get; } = "Rejects a pending invite";
    public override bool IsAdminOnly { get; set; } = false;
}
