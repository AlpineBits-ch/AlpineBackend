namespace Bots.Application.Dtos.Response;

public class BotApplicationCreatedDto
{
    public string ApplicationId { get; set; }

    /// <summary>Also the OAuth client_id and the JWT subject the bot authenticates as.</summary>
    public string ClientId { get; set; }
    public string Name { get; set; }

    /// <summary>Shown exactly once - OpenIddict never re-surfaces it after creation.</summary>
    public string ClientSecret { get; set; }

    /// <summary>
    /// base64(client_id:client_secret) - paste this straight into a Discord bot library's
    /// `Authorization: Bot &lt;token&gt;` header.
    /// </summary>
    public string CompatToken { get; set; }
}
