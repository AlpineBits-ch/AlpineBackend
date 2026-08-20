namespace Guild.Application.Dtos.Request;

public class UpdateChannelDto
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsAgeRestricted { get; set; }

    /// <summary>
    /// Null leaves the channel's privacy alone. Setting it writes the @everyone ViewChannel
    /// overwrite that enforces it, so a PATCH that simply omits the field can no longer publish a
    /// private channel to the whole guild.
    /// </summary>
    public bool? IsPrivate { get; set; }

    public int SlowModeSeconds { get; set; }

    /// <summary>Null leaves the stored icon alone so a client that predates this field cannot wipe it; empty resets it to the channel type's icon.</summary>
    public string? Icon { get; set; }

    /// <summary>Same sentinel as <see cref="Icon"/>: null keeps, empty clears.</summary>
    public string? IconColor { get; set; }
}
