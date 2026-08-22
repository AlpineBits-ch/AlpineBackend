namespace Discovery.Api.Dtos.Request;

/// <summary>The whole interest set a PUT replaces. Topics are wire strings, "tag:..." or "game:...".</summary>
public class UpdateInterestsDto
{
    public IReadOnlyList<string> Topics { get; init; } = [];
    public bool Visible { get; init; } = true;
}
