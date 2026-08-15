using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>What a successful redemption tells the client to do next.</summary>
public class RedeemInviteResultDto
{
    public required string GuildId { get; init; }

    /// <summary>The invite's landing channel, if it named one.</summary>
    public string? ChannelId { get; init; }

    public InviteTargetType TargetType { get; init; }

    /// <summary>Who the invite was about, when it said. Null on an ordinary invite.</summary>
    public string? TargetUserId { get; init; }

    /// <summary>
    /// True when the client should connect to <see cref="ChannelId"/> as voice rather than merely
    /// selecting it.
    /// </summary>
    public bool JoinVoice { get; init; }

    /// <summary>Set when the member's onboarding is still pending, so the client knows to show the
    /// rules gate rather than the channel.</summary>
    public bool OnboardingRequired { get; init; }

    /// <summary>True when this membership ends on disconnect - see
    /// <see cref="Domain.Entity.GuildInvite.Temporary"/>. Worth surfacing: a client that does not say
    /// so leaves the member to discover it by being gone.</summary>
    public bool TemporaryMembership { get; init; }
}
