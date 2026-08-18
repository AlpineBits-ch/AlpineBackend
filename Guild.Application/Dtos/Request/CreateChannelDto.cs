using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateChannelDto
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public string? CategoryId { get; set; }
    public ChannelType Type { get; set; }

    public int Position { get; set; }

    /// <summary>Creates the channel with @everyone denied ViewChannel.</summary>
    public bool IsPrivate { get; set; }
}
