using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateGuildDto
{
    public string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Seeds the guild's feature set from the matching preset. Omitted by v1 clients,
    /// which get Community - the behaviour they already had.</summary>
    public GuildKind Kind { get; set; } = GuildKind.Community;

    public override string ToString()
    {
        return $"Guild: {Name}, Description: {Description}, Kind: {Kind}";
    }
}
