namespace Guild.Application.Dtos.Response;

/// <summary>The answer to watch/unwatch.</summary>
public class WikiWatchStateDto
{
    public string PageId { get; set; } = string.Empty;
    public bool IsWatching { get; set; }
    public int WatcherCount { get; set; }
}
