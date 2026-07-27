using Persistence;

namespace Guild.Domain.Entity;

public class CreateGuildBanParams
{
    public string GuildId { get; set; } = null!;
    public string BannedUserId { get; set; } = null!;
    public string BannedByUserId { get; set; } = null!;
    public string? Reason { get; set; }
}

public class GuildBan : BaseEntity<GuildBan>, IPrefixedEntity
{
    public static string Prefix { get; } = "gban";

    public string GuildId { get; set; } = null!;
    public Aggregates.Guild Guild { get; set; } = null!;

    public string BannedUserId { get; set; } = null!;
    public string BannedByUserId { get; set; } = null!;
    public string? Reason { get; set; }

    public static GuildBan Create(CreateGuildBanParams parameters)
    {
        return new GuildBan
        {
            GuildId = parameters.GuildId,
            BannedUserId = parameters.BannedUserId,
            BannedByUserId = parameters.BannedByUserId,
            Reason = parameters.Reason,
        };
    }
}
