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

    /// <summary>
    /// The raw OAuth client secret. Only populated on first creation - a retry that finds an
    /// existing bot account returns an empty string, since OpenIddict never re-surfaces a
    /// secret once persisted. Callers that hit this should direct the user to reset-secret.
    /// </summary>
    public string ClientSecret { get; set; }
}
