using Persistence;

namespace Guild.Domain.Entity;

/// <summary>A target channel (possibly in a different guild) subscribed to receive copies of
/// published messages from an Announcement-type source channel - Discord's "Follow Channel"
/// concept. Created from the target side (someone with rights in the *receiving* guild chooses to
/// follow a source channel they can see), same direction Discord uses.</summary>
public class CreateGuildChannelFollowParams
{
    public string SourceChannelId { get; set; } = null!;
    public string SourceGuildId { get; set; } = null!;
    public string TargetChannelId { get; set; } = null!;
    public string TargetGuildId { get; set; } = null!;
    public string CreatedByUserId { get; set; } = null!;
}

public class GuildChannelFollow : BaseEntity<GuildChannelFollow>, IPrefixedEntity
{
    public static string Prefix { get; } = "flow";

    public string SourceChannelId { get; set; } = null!;
    public string SourceGuildId { get; set; } = null!;
    public string TargetChannelId { get; set; } = null!;
    public string TargetGuildId { get; set; } = null!;
    public string CreatedByUserId { get; set; } = null!;

    public static GuildChannelFollow Create(CreateGuildChannelFollowParams parameters)
    {
        var date = DateTime.UtcNow;
        return new GuildChannelFollow
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            SourceChannelId = parameters.SourceChannelId,
            SourceGuildId = parameters.SourceGuildId,
            TargetChannelId = parameters.TargetChannelId,
            TargetGuildId = parameters.TargetGuildId,
            CreatedByUserId = parameters.CreatedByUserId,
        };
    }
}
