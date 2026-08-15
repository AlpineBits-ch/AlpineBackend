namespace Guild.Application.Dtos.Request;

public class SetVanityUrlDto
{
    /// <summary>The slug to claim, without any domain or leading slash.</summary>
    public string? VanityUrl { get; set; }
}
