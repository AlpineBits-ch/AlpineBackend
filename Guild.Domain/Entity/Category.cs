using System.ComponentModel.DataAnnotations.Schema;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateCategoryParams
{
    public string Name { get; set; }
    public string GuildId { get; set; }
    public int Position { get; set; } = 0;
}

public class Category : BaseEntity<Category>, IPrefixedEntity
{
    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
    public string Name { get; set; }
    public int Position { get; set; } = 0;
    [NotMapped] public static string Prefix { get; } = "cate";

    public virtual Aggregates.Guild Guild { get; set; } = null!;
    public string GuildId { get; set; }
    
    public ICollection<ChannelPermission> Permissions { get; set; } = new List<ChannelPermission>();


    public static Category Create(CreateCategoryParams dto)
    {
        var id = GenerateId();
        return new Category
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Name = dto.Name,
            GuildId = dto.GuildId,
            Position = dto.Position,
        };
    }
    
    public static ICollection<Category> GetDefault(string guildId)
    {
        var date = DateTime.UtcNow;
        
        var textId = GenerateId();
        var text = new Category
        {
            Id = textId,
            CreatedAt = date,
            UpdatedAt = date,
            Name = "Text Channels",
            GuildId = guildId,
            Position = 0,
        };
        var voiceId = GenerateId();
        var voice = new Category
        {
            Id = voiceId,
            Name = "Voice Channels",
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = guildId,
            Position = 1,
        };

        var voiceChannel = Channel.Create(new CreateChannelParams()
        {
            Name = "general",
            Type = ChannelType.Voice,
            CategoryId = voiceId,
            GuildId = guildId,
            Description = "General voice channel",
            Position = 0
        });
        
        var textChannel = Channel.Create(new CreateChannelParams()
        {
            Name = "general",
            Type = ChannelType.Text,
            CategoryId = textId,
            GuildId = guildId,
            Description = "General text channel",
            Position = 0
        });
        voice.Channels.Add(voiceChannel);
        text.Channels.Add(textChannel);
        
        return new List<Category> { text, voice };
    }
    
}