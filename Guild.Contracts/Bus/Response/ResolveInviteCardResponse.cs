namespace Guild.Contracts.Bus.Response;

/// <summary>Null <see cref="Invite"/> means no such code.</summary>
public class ResolveInviteCardResponse
{
    public InviteCardInfo? Invite { get; set; }
}

/// <summary>Immutable facts only.</summary>
public class InviteCardInfo
{
    public string Code { get; set; } = "";

    public string GuildId { get; set; } = "";

    public string GuildName { get; set; } = "";

    public string? GuildDescription { get; set; }

    /// <summary>The channel the invite lands a joining member on, when it names one.</summary>
    public string? ChannelId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public int? MaxUses { get; set; }
}
