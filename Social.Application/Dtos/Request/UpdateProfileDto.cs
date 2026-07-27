namespace Social.Api.Dtos.Request;

public class UpdateProfileDto
{
    public string? Bio { get; set; }

    // Hex color, e.g. "#5865F2". Send an empty string to clear it.
    public string? AccentColor { get; set; }

    // Name of a Social.Domain.Enums.ProfileFont value.
    public string? Font { get; set; }
}
