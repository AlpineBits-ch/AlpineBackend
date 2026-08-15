namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Published alongside the "guild.InviteCreated" SignalR broadcast in InviteEndpoint, so the bot
/// gateway can dispatch Discord's <c>INVITE_CREATE</c>.
/// </summary>
public class InviteCreatedForBots
{
    public string GuildId { get; set; }

    /// <summary>The short shareable code, which is what a Discord-shaped <c>INVITE_CREATE</c> keys
    /// on. Note that it is a secret in the ordinary sense: anyone holding it can join.</summary>
    public string Code { get; set; }

    public string InviteId { get; set; }

    /// <summary>The invite's landing channel, if it named one. Null is legal and common.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Who created it.</summary>
    public string? InviterId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Null means unlimited, matching the column.</summary>
    public int? MaxUses { get; set; }

    public int Uses { get; set; }

    /// <summary>Temporary membership. Discord's <c>temporary</c> field.</summary>
    public bool Temporary { get; set; }

    /// <summary><c>None</c> or <c>VoiceChannel</c>, as the name of the
    /// <c>Guild.Domain.Enums.InviteTargetType</c> member. A string rather than the enum so the
    /// contract does not force Bots to reference Guild.Domain, and so adding a target type is not a
    /// breaking deserialization change for a gateway that has not been redeployed.</summary>
    public string TargetType { get; set; }

    public string? TargetUserId { get; set; }
}

/// <summary>
/// Published alongside the "guild.InviteDeleted" SignalR broadcast, for Discord's
/// <c>INVITE_DELETE</c>.
/// </summary>
public class InviteDeletedForBots
{
    public string GuildId { get; set; }
    public string Code { get; set; }
    public string InviteId { get; set; }
    public string? ChannelId { get; set; }
}
