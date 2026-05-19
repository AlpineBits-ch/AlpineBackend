using Guild.Domain.Aggregates;
using Persistence;

namespace Guild.Domain.Entity;

public class WebhookConfig : BaseEntity<WebhookConfig>, IPrefixedEntity
{
    public static string Prefix { get; } = "weco";
    public virtual Aggregates.Guild Guild { get; set; }
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public virtual Channel Channel { get; set; }
    public string CreatedBy { get; set; }
    public string Name { get; set; } = "Captain Hook";
}