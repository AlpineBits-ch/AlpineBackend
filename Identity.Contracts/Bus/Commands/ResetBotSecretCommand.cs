namespace Identity.Contracts.Bus.Commands;

public class ResetBotSecretCommand
{
    public string BotUserId { get; set; }
}

public class ResetBotSecretResponse
{
    public bool Found { get; set; }
    public string ClientSecret { get; set; }
}
