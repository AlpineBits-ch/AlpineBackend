namespace Bots.Application.Dtos.Response;

public class ResetSecretDto
{
    public string ClientId { get; set; }
    public string ClientSecret { get; set; }
    public string CompatToken { get; set; }
}
