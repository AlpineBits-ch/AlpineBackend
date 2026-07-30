using System.Security.Cryptography;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
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

    /// <summary>Default avatar for messages this webhook posts. Overridable per execution, the
    /// same way <see cref="Name"/> is.</summary>
    public string? AvatarUrl { get; set; }

    public WebhookType Type { get; set; } = WebhookType.Incoming;

    /// <summary>
    /// The webhook's standing credential. This *is* the authentication for the execute endpoint -
    /// there is no bearer token on that call, matching Discord (and matching how this codebase
    /// already treats interaction tokens; see PendingInteractionStore's remarks). Consequences:
    ///
    /// - It is a bearer secret granting write access to one channel indefinitely, so it may only
    ///   ever be returned to callers holding ManageWebhooks, and <see cref="Regenerate"/> exists
    ///   to rotate it when it leaks.
    /// - It is random bytes rather than the Ksuid used for entity ids: ids encode a timestamp and
    ///   are handed out freely, which is exactly the property a credential must not have.
    /// </summary>
    public string Token { get; set; } = null!;

    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static WebhookConfig Create(string guildId, string channelId, string createdBy, string name, string? avatarUrl = null)
    {
        var date = DateTimeOffset.UtcNow;
        return new WebhookConfig
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = guildId,
            ChannelId = channelId,
            CreatedBy = createdBy,
            Name = name,
            AvatarUrl = avatarUrl,
            Type = WebhookType.Incoming,
            Token = GenerateToken(),
        };
    }

    /// <summary>Rotates the token, invalidating every integration currently posting through it.
    /// The only remedy once a token has leaked, since the id stays the same.</summary>
    public void Regenerate()
    {
        Token = GenerateToken();
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
