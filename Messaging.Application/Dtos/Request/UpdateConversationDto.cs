namespace Messaging.Application.Dtos.Request;

/// <summary>Edits a group conversation's own properties, as opposed to the caller's settings on it.</summary>
public class UpdateConversationDto
{
    /// <summary>The new group name. Blank clears it, which puts the member list back in the title.</summary>
    public string? Name { get; set; }
}
