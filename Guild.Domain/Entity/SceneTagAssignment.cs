namespace Guild.Domain.Entity;

/// <summary>
/// Join row between a scene (a Channel of type Scene) and a <see cref="SceneTag"/>.
/// </summary>
public class SceneTagAssignment
{
    public string SceneChannelId { get; set; } = null!;

    public string TagId { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
