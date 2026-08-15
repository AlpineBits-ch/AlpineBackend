using System.Security.Cryptography;
using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateGuildInviteParams
{
    public string GuildId { get; set; }
    public InviteType Type { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public string? ChannelId { get; set; }
    public string? InviterId { get; set; }
    public bool Temporary { get; set; }
    public InviteTargetType TargetType { get; set; } = InviteTargetType.None;
    public string? TargetUserId { get; set; }
}

public class GuildInvite : BaseEntity<GuildInvite>, IPrefixedEntity
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // excludes ambiguous 0/O, 1/I/L

    public static string Prefix { get; } = "chiv";
    public string GuildId { get; set; }
    public Aggregates.Guild Guild { get; set; }

    public InviteType Type { get; set; }

    /// <summary>The <i>stored</i> state, and only ever the part of it somebody decided: a one-time
    /// invite that has been consumed, or a revocation. Expiry is not written here - read
    /// <see cref="EffectiveState"/> instead, and see its remarks for why.</summary>
    public InviteState State { get; set; }

    /// <summary>Short human-shareable code, distinct from Id, used for the public lookup route.</summary>
    public string Code { get; set; } = null!;

    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int UseCount { get; set; }

    /// <summary>Optional channel the invite should land the joining member's client on; purely
    /// advisory metadata (no server-side routing depends on it) <i>unless</i>
    /// <see cref="TargetType"/> names a target that gives it meaning - see
    /// <see cref="InviteTargetType.VoiceChannel"/>.</summary>
    public string? ChannelId { get; set; }
    public Aggregates.Channel? Channel { get; set; }

    /// <summary>The user who created this invite.</summary>
    public string? InviterId { get; set; }

    /// <summary>
    /// Temporary membership: a member who joins on this invite is removed again once they go
    /// offline, unless they have been given a role beyond @everyone in the meantime.
    /// </summary>
    public bool Temporary { get; set; }

    /// <summary>What this invite is an invite to beyond the guild.</summary>
    public InviteTargetType TargetType { get; set; } = InviteTargetType.None;

    /// <summary>The user this invite is <i>about</i> - whose stream or call the redeemer is being
    /// invited to join. Advisory: it is rendered on the preview so the recipient knows who is on
    /// the other end, and nothing gates redemption on it.</summary>
    public string? TargetUserId { get; set; }

    /// <summary>When the invite was revoked, if it was.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public ICollection<GuildMember> Members { get; set; } = new List<GuildMember>();

    public static GuildInvite Create(CreateGuildInviteParams parameters)
    {
        var date = DateTime.UtcNow;
        return new GuildInvite
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = parameters.GuildId,
            Type = parameters.Type,
            State = InviteState.Active,
            Code = GenerateCode(),
            ExpiresAt = parameters.ExpiresAt,
            MaxUses = parameters.MaxUses,
            ChannelId = parameters.ChannelId,
            InviterId = parameters.InviterId,
            Temporary = parameters.Temporary,
            TargetType = parameters.TargetType,
            TargetUserId = parameters.TargetUserId,
            UseCount = 0,
        };
    }

    public static string GenerateCode() => RandomNumberGenerator.GetString(CodeAlphabet, 8);

    public bool IsExhausted() => MaxUses is not null && UseCount >= MaxUses;
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && ExpiresAt <= now;

    /// <summary>
    /// The state a client should be shown, as opposed to the one stored in <see cref="State"/>.
    /// </summary>
    public InviteState EffectiveState(DateTimeOffset now) =>
        State is InviteState.Revoked or InviteState.Expired ? State
        : IsExpired(now) || IsExhausted() ? InviteState.Expired
        : InviteState.Active;

    /// <summary>Whether a redemption may proceed.</summary>
    public bool IsRedeemable(DateTimeOffset now) => EffectiveState(now) == InviteState.Active;

    public void Revoke(DateTimeOffset now)
    {
        State = InviteState.Revoked;
        RevokedAt = now;
        UpdatedAt = now;
    }
}
