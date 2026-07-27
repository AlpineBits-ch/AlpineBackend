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
}

public class GuildInvite : BaseEntity<GuildInvite>, IPrefixedEntity
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // excludes ambiguous 0/O, 1/I/L

    public static string Prefix { get; } = "chiv";
    public string GuildId { get; set; }
    public Aggregates.Guild Guild { get; set; }

    public InviteType Type { get; set; }
    public InviteState State { get; set; }

    /// <summary>Short human-shareable code, distinct from Id, used for the public lookup route.</summary>
    public string Code { get; set; } = null!;

    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int UseCount { get; set; }

    /// <summary>Optional channel the invite should land the joining member's client on; purely
    /// advisory metadata (no server-side routing depends on it).</summary>
    public string? ChannelId { get; set; }
    public Aggregates.Channel? Channel { get; set; }

    public ICollection<GuildMember> Members { get; set; } = new List<GuildMember>();

    public static GuildInvite Create(CreateGuildInviteParams parameters)
    {
        return new GuildInvite
        {
            GuildId = parameters.GuildId,
            Type = parameters.Type,
            State = InviteState.Active,
            Code = GenerateCode(),
            ExpiresAt = parameters.ExpiresAt,
            MaxUses = parameters.MaxUses,
            ChannelId = parameters.ChannelId,
            UseCount = 0,
        };
    }

    public static string GenerateCode() => RandomNumberGenerator.GetString(CodeAlphabet, 8);

    public bool IsExhausted() => MaxUses is not null && UseCount >= MaxUses;
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && ExpiresAt <= now;
}