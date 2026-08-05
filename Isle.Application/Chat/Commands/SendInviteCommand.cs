using Isle.Api.Services;
using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;
using Isle.Api.Services.State;

namespace Isle.Api.Chat.Commands;

public class SendInviteCommand(
    MicroserviceContext microserviceContext,
    IBridgeClient bridgeClient,
    PlayerPresenceManager presence,
    PlayerSpawnTracker spawnTracker) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var identifier = string.Join(' ', context.Arguments).Trim();
        if (identifier.Length == 0)
        {
            return "Usage: !invite in-game name | friendly id | steam id";
        }

        if (string.IsNullOrWhiteSpace(context.PlayerSpecies))
        {
            return "You need to be spawned in to send an invite.";
        }

        var lastSpawn = await spawnTracker.GetLastSpawnAsync(context.PlayerSteam);
        var (eligible, eligibilityError) = InviteTeleportEligibility.Check(context.PlayerGrowth, lastSpawn);
        if (!eligible)
        {
            return eligibilityError!;
        }

        var resolution = await PlayerResolver.ResolveAsync(microserviceContext, identifier);
        switch (resolution.Outcome)
        {
            case PlayerResolver.ResolveOutcome.Ambiguous:
                return "Multiple players share that in-game name, please use friendly id.";
            case PlayerResolver.ResolveOutcome.NotFound:
                return $"No player found for \"{identifier}\".";
        }

        var target = resolution.Player!;
        if (target.Id == context.PlayerId)
        {
            return "You can not send an invite to yourself.";
        }

        if (!presence.IsPlayerOnline(target.Id))
        {
            return $"{target.InGameName ?? "That player"} is not online right now.";
        }

        var alreadyPending = await microserviceContext.PlayerInvites.AnyAsync(r =>
            r.SenderPlayerId == context.PlayerId &&
            r.ReceiverPlayerId == target.Id &&
            r.Status == PlayerInviteStatus.Pending);
        if (alreadyPending)
        {
            return $"You already have a pending invite to {target.InGameName ?? "that player"}.";
        }

        var sender = await microserviceContext.Players.FirstOrDefaultAsync(p => p.Id == context.PlayerId);
        if (sender is null)
        {
            return "There was an issue sending your invite (404), please report that on our discord!";
        }

        var invite = PlayerInvite.Create(sender.Id, target.Id);
        microserviceContext.PlayerInvites.Add(invite);
        await microserviceContext.SaveChangesAsync();

        // Let the receiver know how to accept - they teleport to us on accept.
        var senderLabel = string.IsNullOrWhiteSpace(sender.InGameName) ? sender.FriendlyId : sender.InGameName;
        await bridgeClient.DmAsync(
            text: $"{senderLabel} invited you! Reply !accept {sender.FriendlyId} to teleport to them.",
            steam: target.SteamId,
            sender: "VENTA.GG",
            mode: ChatMode.Spatial);

        return $"Invite sent to {target.InGameName ?? target.FriendlyId}.";
    }

    public override string Name { get; } = "invite";
    public override string Description { get; } = "Invites a player; on accept they teleport to you";
    public override bool IsAdminOnly { get; set; } = false;
    public override TimeSpan Cooldown => TimeSpan.FromSeconds(30);
}
