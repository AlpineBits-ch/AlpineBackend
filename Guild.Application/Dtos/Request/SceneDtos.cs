using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>
/// Opens a scene under a text channel, together with its out-of-character companion thread.
/// </summary>
public class CreateSceneDto
{
    /// <summary>The scene's title. Free text, so "The Siege of Blackwater" is legal here in a way
    /// an ordinary channel name is not.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The OOC thread's title, defaulting to the scene's name with an OOC marker.</summary>
    public string? OocName { get; set; }

    /// <summary>The starting cast, as personas already adopted into this guild. Omit to take the
    /// cast from <see cref="TurnOrder"/>.</summary>
    public List<string>? ParticipantPersonaIds { get; set; }

    /// <summary>The rotation, if it should differ from the order the cast was given in.</summary>
    public List<string>? TurnOrder { get; set; }

    /// <summary>How long each turn gets. Omit for a scene that is not on a clock and is never
    /// nudged.</summary>
    public int? TurnLengthHours { get; set; }

    /// <summary>Open for a scene the cast still has to arrive in, Active to start the first turn
    /// straight away. Nothing else is legal at creation.</summary>
    public SceneStatus? Status { get; set; }
}

/// <summary>
/// A partial update: an omitted property leaves the scene's current value alone.
/// </summary>
public class UpdateSceneDto
{
    public SceneStatus? Status { get; set; }

    /// <summary>Omit to leave the clock alone, send <c>null</c> to take the turn off the
    /// clock.</summary>
    public Optional<DateTimeOffset> TurnDeadlineAt { get; set; }

    public Optional<int> TurnLengthHours { get; set; }

    /// <summary>Replaces the cast. Characters it drops leave the scene, and the turn is handed on
    /// when it was theirs. Applied before <see cref="TurnOrder"/> is checked against it.</summary>
    public List<string>? ParticipantPersonaIds { get; set; }

    /// <summary>Replaces the rotation. Every entry must already be in the cast.</summary>
    public List<string>? TurnOrder { get; set; }

    /// <summary>Hands the turn to a named character outright, for the cases the rotation cannot
    /// express.</summary>
    public string? CurrentTurnPersonaId { get; set; }

    /// <summary>The closing line for a scene being concluded, which is otherwise dropped.</summary>
    public string? ConclusionNote { get; set; }
}

/// <summary>Adds one character to a scene's cast.</summary>
public class AddSceneParticipantDto
{
    public required string PersonaId { get; set; }

    /// <summary>Where in the rotation to insert them, appending when omitted.</summary>
    public int? Position { get; set; }
}
