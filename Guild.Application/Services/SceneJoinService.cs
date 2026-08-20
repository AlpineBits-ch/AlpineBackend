using System.Text;
using Echo.Realtime;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.SignalR;
using Wolverine;
using MessagingAuthorIdType = Messaging.Contracts.Bus.Commands.AuthorIdType;
using MessagingMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Guild.Application.Services;

/// <summary>
/// Walking a character into a scene and out of it: the cast change, the line it writes in the log,
/// and the events the GM's banner follows.
/// </summary>
public class SceneJoinService(
    MicroserviceContext ctx,
    SceneService scenes,
    SceneVisibilityCache visibility,
    GuildPermissionService permissions,
    PersonaCastService cast,
    ModulePermissionHolderService holders,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus)
{
    /// <summary>The word a GM's removal carries, so one message type covers both cases.</summary>
    public const string RemovedContent = "removed";

    /// <summary>
    /// Puts a character in the cast and writes the line that says so.
    /// </summary>
    /// <param name="scene">The scene's turn state, tracked.</param>
    /// <param name="personaId">The character joining.</param>
    /// <param name="actorUserId">The account the system message is authored by.</param>
    /// <param name="now">The instant it joined.</param>
    /// <returns>False when the character was already in the scene.</returns>
    public async Task<bool> JoinAsync(
        SceneState scene, string personaId, string actorUserId, DateTimeOffset now)
    {
        if (!scene.AddParticipant(personaId, now)) return false;

        await AnnounceJoinedAsync(scene, personaId, actorUserId);
        await scenes.BroadcastUpdatedAsync(scene);

        return true;
    }

    /// <summary>
    /// Puts a character that just spoke into a scene it was not in: into the rotation when the
    /// scene is open, into the cast alone when a game master spoke it into a closed one.
    /// </summary>
    /// <param name="scene">The scene's turn state, tracked.</param>
    /// <param name="personaId">The character the message went out as, or null for a plain message.</param>
    /// <param name="authorUserId">The account that posted.</param>
    /// <param name="now">The instant the message was created.</param>
    /// <returns>True when the character joined.</returns>
    public async Task<bool> AutoJoinOnPostAsync(
        SceneState scene, string? personaId, string authorUserId, DateTimeOffset now)
    {
        if (scene.Status == SceneStatus.Concluded) return false;
        if (personaId is null || scene.IsInCast(personaId)) return false;

        // An open scene takes the character into the rotation: it turned up, so it plays.
        if (scene.JoinPolicy == SceneJoinPolicy.Open)
            return await JoinAsync(scene, personaId, authorUserId, now);

        // A closed scene is only reachable here by whoever the send gate let through, which is a
        // game master voicing somebody. The cast has to say they spoke; the queue must not hand a
        // one-line NPC a turn.
        if (!await permissions.CanUserPerformActionOnGuildAsync(
                authorUserId, scene.GuildId, ModulePermissions.ManageScenes))
        {
            return false;
        }

        if (!scene.AddGuest(personaId, now)) return false;

        await AnnounceJoinedAsync(scene, personaId, authorUserId);
        await scenes.BroadcastUpdatedAsync(scene);

        return true;
    }

    /// <summary>
    /// Writes the joined line and drops the visibility map, for the routes that change the cast
    /// themselves because they also place the character in the rotation.
    /// </summary>
    /// <param name="scene">The scene, with the character already in its cast.</param>
    /// <param name="personaId">The character that joined.</param>
    /// <param name="actorUserId">The account the system message is authored by.</param>
    public async Task AnnounceJoinedAsync(SceneState scene, string personaId, string actorUserId)
    {
        if (scene.Visibility == SceneVisibility.Cast)
            await visibility.InvalidateGuildAsync(scene.GuildId);

        await WriteSystemMessageAsync(
            scene, personaId, actorUserId, MessagingMessageType.SceneCharacterJoined, content: "");
    }

    /// <summary>The <see cref="AnnounceJoinedAsync"/> twin for a character leaving.</summary>
    /// <param name="scene">The scene, with the character already out of its cast.</param>
    /// <param name="personaId">The character that left.</param>
    /// <param name="actorUserId">The account the system message is authored by.</param>
    /// <param name="removedByGameMaster">True when a GM took it out rather than the player leaving.</param>
    public async Task AnnounceLeftAsync(
        SceneState scene, string personaId, string actorUserId, bool removedByGameMaster)
    {
        if (scene.Visibility == SceneVisibility.Cast)
            await visibility.InvalidateGuildAsync(scene.GuildId);

        await WriteSystemMessageAsync(
            scene, personaId, actorUserId, MessagingMessageType.SceneCharacterLeft,
            removedByGameMaster ? RemovedContent : "");
    }

    /// <summary>
    /// Takes a character out of the cast, handing the turn on when it was theirs, and writes the
    /// line that says so.
    /// </summary>
    /// <param name="scene">The scene's turn state, tracked.</param>
    /// <param name="personaId">The character leaving.</param>
    /// <param name="actorUserId">The account the system message is authored by.</param>
    /// <param name="removedByGameMaster">True when a GM took it out rather than the player leaving.</param>
    /// <param name="now">The instant it left.</param>
    /// <returns>False when the character was not in the scene.</returns>
    public async Task<bool> LeaveAsync(
        SceneState scene, string personaId, string actorUserId, bool removedByGameMaster,
        DateTimeOffset now)
    {
        var wasTheirTurn = string.Equals(scene.CurrentTurnPersonaId, personaId, StringComparison.Ordinal);

        var unavailable = await scenes.UnavailablePersonasAsync(scene.GuildId, scene.Rotation, now);
        if (!scene.RemoveParticipant(personaId, unavailable, now)) return false;

        await AnnounceLeftAsync(scene, personaId, actorUserId, removedByGameMaster);

        if (wasTheirTurn) await scenes.BroadcastTurnAsync(scene, personaId);
        await scenes.BroadcastUpdatedAsync(scene);

        return true;
    }

    /// <summary>Tells whoever runs scenes here that somebody is asking to get in.</summary>
    /// <param name="request">The new row.</param>
    public async Task RequestedAsync(SceneJoinRequest request)
    {
        var recipients = await holders.HoldersAsync(request.GuildId, ModulePermissions.ManageScenes);
        if (recipients.Count == 0) return;

        var persona = (await cast.ResolveAsync(request.GuildId, [request.PersonaId]))
            .GetValueOrDefault(request.PersonaId);

        await hub.Clients.Users(recipients).SendAsync(SceneService.JoinRequestedEvent, new
        {
            GuildId = request.GuildId,
            ChannelId = request.SceneChannelId,
            RequestId = request.Id,
            PersonaId = request.PersonaId,
            PersonaName = persona?.Name ?? "",
            PersonaAvatarUrl = persona?.AvatarUrl,
            PersonaColor = persona?.Color,
            RequestedByUserId = request.RequestedByUserId,
            Note = request.Note,
            CreatedAt = request.CreatedAt,
        });
    }

    /// <summary>
    /// Tells the player their answer, and the GMs so a second banner clears itself. The reason is
    /// feedback for the player and travels on this event only.
    /// </summary>
    /// <param name="request">The decided row.</param>
    public async Task ResolvedAsync(SceneJoinRequest request)
    {
        var recipients = new HashSet<string>(
            await holders.HoldersAsync(request.GuildId, ModulePermissions.ManageScenes),
            StringComparer.Ordinal) { request.RequestedByUserId };

        await hub.Clients.Users(recipients.ToList()).SendAsync(SceneService.JoinRequestResolvedEvent, new
        {
            GuildId = request.GuildId,
            ChannelId = request.SceneChannelId,
            RequestId = request.Id,
            PersonaId = request.PersonaId,
            Status = request.Status.ToString(),
            DecisionReason = request.DecisionReason,
            DecidedByUserId = request.DecidedByUserId,
        });
    }

    /// <summary>
    /// The scene's own log line, authored by the real account with the character's display fields
    /// denormalised onto it: a character renamed a year later still reads correctly at the point
    /// where it walked in.
    /// </summary>
    private async Task WriteSystemMessageAsync(
        SceneState scene, string personaId, string actorUserId, MessagingMessageType type,
        string content)
    {
        var persona = (await cast.ResolveAsync(scene.GuildId, [personaId]))
            .GetValueOrDefault(personaId);

        await bus.InvokeAsync(new CreateMessageCommand
        {
            Content = Encoding.UTF8.GetBytes(content),
            ChannelId = scene.ChannelId,
            AuthorId = actorUserId,
            AuthorIdType = MessagingAuthorIdType.Persona,
            AuthorDisplayName = persona?.Name,
            AuthorAvatarUrl = persona?.AvatarUrl,
            PersonaId = personaId,
            Type = type,
            Mentions = [],
        });
    }
}
