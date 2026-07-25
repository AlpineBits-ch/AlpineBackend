using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using FluentValidation;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Channel;
using Guild.Domain.Validators;
using Persistence;

namespace Guild.Domain.Aggregates;

public class CreateChannelParams
{
    public string Name { get; init; }
    public string Description { get; init; }
    public ChannelType Type { get; init; } = ChannelType.Text;
    public string? CategoryId { get; init; }
    public string GuildId { get; init; }
    public int Position { get; init; } = 0;
}

public class Channel : Aggregate<Channel>, IPrefixedEntity
{
    public required string Name { get; set; }
    public  string? Description { get; set; }
    public required ChannelType Type { get; set; } = ChannelType.Text;
    
    public string? CategoryId { get; set; }
    public virtual Category? Category { get; set; }
    public string GuildId { get; set; }
    public virtual Guild Guild { get; set; } = null!;
    
    // Inbound navigation property
    public virtual Guild? SystemChannelGuild { get; set; }
    
    public bool IsAgeRestricted { get; set; }
    public bool IsPrivate { get; set; }
    public int Position { get; set; } = 0;

    public ICollection<ChannelPermission> Permissions { get; set; } = [];
    
    public virtual ICollection<ReadState> ReadStates { get; set; } = [];
    
    public virtual ICollection<WebhookConfig> WebhookConfigs { get; set; } = [];
    
    [NotMapped] public static string Prefix { get; } = "chan";

    public virtual ICollection<PublicKeyStore> PublicKeys { get; set; } = [];

    public EncryptionState EncryptionState { get; init; } = EncryptionState.Plain;

    public static Channel Create(CreateChannelParams @params)
    {
        var id = GenerateId();
        
        var date = DateTime.UtcNow;
        var channel = new Channel()
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            Name = @params.Name,
            Description = @params.Description,
            Type = @params.Type,
            CategoryId = @params.CategoryId,
            GuildId = @params.GuildId,
            Position = @params.Position,
        };

        channel.AddDomainEvent(new ChannelCreated() { ChannelId = id, GuildId = @params.GuildId });

        new ChannelValidator().ValidateAndThrow(channel);
        
        return channel;
    }
    
}