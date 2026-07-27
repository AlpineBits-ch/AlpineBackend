namespace Identity.Contracts.Bus.Commands;

public class CreateBotAccountCommand
{
    /// <summary>
    /// Caller-generated (by the Bots service, not Identity) so the whole create-application
    /// flow is idempotent under retry.
    /// </summary>
    public string BotUserId { get; set; }
    public string Name { get; set; }
}

public class CreateBotAccountResponse
{
    public string BotUserId { get; set; }

    /// <summary>The raw OAuth client secret.</summary>
    public string ClientSecret { get; set; }
}
