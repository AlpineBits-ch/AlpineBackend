namespace Guild.Application.Dtos.Request;

public class CategoryPositionDto
{
    public string CategoryId { get; set; }
    public int Position { get; set; }
}

public class ChannelPositionDto
{
    public string ChannelId { get; set; }
    public int Position { get; set; }
    /// <summary>
    /// Null places the channel at the guild root; a valid category ID nests it inside that category.
    /// </summary>
    public string? CategoryId { get; set; }
}

public class ReorderChannelsDto
{
    public List<CategoryPositionDto> Categories { get; set; } = [];
    public List<ChannelPositionDto> Channels { get; set; } = [];
}
