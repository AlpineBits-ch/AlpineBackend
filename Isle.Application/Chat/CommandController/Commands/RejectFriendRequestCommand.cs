using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.CommandController.Commands;

public class RejectFriendRequestCommand(MicroserviceContext microserviceContext) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var pending = await microserviceContext.FriendRequests
            .Include(r => r.SenderPlayer)
            .Where(r => r.ReceiverPlayerId == context.PlayerId && r.Status == FriendRequestStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        if (pending.Count == 0)
        {
            return "You have no pending friend requests.";
        }

        var identifier = string.Join(' ', context.Arguments).Trim();
        FriendRequest request;

        if (identifier.Length == 0)
        {
            if (pending.Count > 1)
            {
                var senders = string.Join(", ", pending.Select(r =>
                    $"{r.SenderPlayer.InGameName ?? r.SenderPlayer.FriendlyId} ({r.SenderPlayer.FriendlyId})"));
                return $"You have {pending.Count} requests. Reject one with !reject <friendly id>. From: {senders}";
            }

            request = pending[0];
        }
        else
        {
            var resolution = await PlayerResolver.ResolveAsync(microserviceContext, identifier);
            if (resolution.Outcome == PlayerResolver.ResolveOutcome.Ambiguous)
            {
                return "Multiple players share that in-game name, pls use friendly id.";
            }

            var match = resolution.Player is { } sender
                ? pending.FirstOrDefault(r => r.SenderPlayerId == sender.Id)
                : null;
            if (match is null)
            {
                return $"You have no pending request from \"{identifier}\".";
            }

            request = match;
        }

        request.Reject();
        await microserviceContext.SaveChangesAsync();

        return $"Rejected the friend request from {request.SenderPlayer.InGameName ?? request.SenderPlayer.FriendlyId}.";
    }

    public override string Name { get; } = "reject";
    public override string Description { get; } = "Rejects a pending friend request";
    public override bool IsAdminOnly { get; set; } = false;
}
