using Isle.Api.Services;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Chat.CommandController.Commands;

public class AcceptFriendRequestCommand(
    MicroserviceContext microserviceContext,
    IBridgeClient bridgeClient,
    PlayerPresenceManager presence,
    PlayerSpawnTracker spawnTracker) : ChatCommand
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
                return $"You have {pending.Count} requests. Accept one with !accept <friendly id>. From: {senders}";
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

        // The acceptee is the one being teleported, so they must meet the fresh-spawn gate.
        if (string.IsNullOrWhiteSpace(context.PlayerSpecies))
        {
            return "You need to be spawned in to accept a friend request.";
        }

        var lastSpawn = await spawnTracker.GetLastSpawnAsync(context.PlayerSteam);
        var (eligible, eligibilityError) = FriendTeleportEligibility.Check(context.PlayerGrowth, lastSpawn);
        if (!eligible)
        {
            return eligibilityError!;
        }

        var initiator = request.SenderPlayer;
        if (!presence.IsPlayerOnline(initiator.Id))
        {
            return $"{initiator.InGameName ?? "That player"} is no longer online.";
        }

        PositionData initiatorPos;
        try
        {
            initiatorPos = await bridgeClient.GetPosAsync(initiator.SteamId);
        }
        catch (Exception)
        {
            return "Couldn't locate your friend to teleport to. Try again in a moment.";
        }

        var teleport = await bridgeClient.TeleportAsync(
            context.PlayerSteam,
            initiatorPos.Pos.X,
            initiatorPos.Pos.Y,
            initiatorPos.Pos.Z,
            initiatorPos.Rot?.Yaw);

        if (!teleport.Ok)
        {
            return "Teleport failed, please try again in a moment.";
        }

        request.Accept();
        await microserviceContext.SaveChangesAsync();

        await bridgeClient.DmAsync(
            text: $"{context.PlayerName} accepted your friend request and teleported to you.",
            steam: initiator.SteamId,
            sender: "VENTA.GG",
            mode: ChatMode.Spatial);

        return $"Teleported to {initiator.InGameName ?? initiator.FriendlyId}.";
    }

    public override string Name { get; } = "accept";
    public override string Description { get; } = "Accepts a friend request and teleports you to them";
    public override bool IsAdminOnly { get; set; } = false;
}
