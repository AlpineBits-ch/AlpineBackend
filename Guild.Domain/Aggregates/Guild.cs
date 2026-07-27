using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Guild;
using Persistence;

namespace Guild.Domain.Aggregates;

public class CreateGuildParams
{
    public string Name { get; init; }
    public string? Description { get; init; }
    public string OwnerId { get; init; }
    public required string OwnerSearchValue { get; init; }
    public string? OwnerNickname { get; init; }
}

public class Guild : Aggregate<Guild>, IPrefixedEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    
    public string OwnerId { get; init; }
    public virtual ICollection<Channel> Channels { get; set; } = new List<Channel>();
    public virtual ICollection<Role> Roles { get; set; } = new List<Role>();
    public virtual ICollection<Category> Categories { get; set; } = new List<Category>();

    [NotMapped] public static string Prefix { get; } = "gild";
    
    public virtual ICollection<PublicKeyStore> PublicKeys { get; set; } = [];
    public EncryptionState EncryptionState { get; init; } = EncryptionState.Plain;
    public ICollection<GuildMember> Members { get; set; } = new List<GuildMember>();

    public ICollection<GuildInvite> Invites { get; set; } = new List<GuildInvite>();
    
    public virtual Channel? SystemChannel { get; set; }
    
    public virtual ICollection<WebhookConfig> WebhookConfigs { get; set; }
    public string? SystemChannelId { get; set; }

    public static Guild Create(CreateGuildParams parameters)
    {
        var id = Guild.GenerateId();
        var memberId = GuildMember.GenerateId();
        var date = DateTime.UtcNow;
        var guild = new Guild()
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,   
            Name = parameters.Name,
            Description = parameters.Description,
            OwnerId = parameters.OwnerId,
            Categories = Category.GetDefault(id),
            Members = [new GuildMember()
            {
                Id = memberId,
                CreatedAt = date,
                UpdatedAt = date,
                UserId = parameters.OwnerId,
                JoinedAt = date,
                GuildId = id,
                Nickname = parameters.OwnerNickname,
                SearchValue = parameters.OwnerSearchValue,
            }],
            Roles = [Role.CreateEveryoneRole(id, memberId)]
        };

        guild.SystemChannelId = guild.Categories.First(c => c.Position == 0).Channels.First(c => c.Type == ChannelType.Text).Id;
        
        guild.AddDomainEvent(new GuildCreated() { GuildId = id });
        return guild;
    }


}