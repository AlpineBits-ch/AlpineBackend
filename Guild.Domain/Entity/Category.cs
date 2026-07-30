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

    /// <summary>The channel tree a brand-new guild starts with.</summary>
    public static ICollection<Category> GetDefaultForKind(GuildKind kind, string guildId) =>
        kind == GuildKind.Household ? GetHouseholdDefault(guildId) : GetDefault(guildId);

    private static ICollection<Category> GetHouseholdDefault(string guildId)
    {
        var date = DateTime.UtcNow;

        Category MakeCategory(string name, int position) => new()
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            Name = name,
            GuildId = guildId,
            Position = position,
        };

        var home = MakeCategory("Home", 0);
        var house = MakeCategory("House", 1);
        var voice = MakeCategory("Voice", 2);

        void Add(Category category, string name, ChannelType type, string description, int position) =>
            category.Channels.Add(Channel.Create(new CreateChannelParams
            {
                Name = name,
                Type = type,
                CategoryId = category.Id,
                GuildId = guildId,
                Description = description,
                Position = position,
            }));

        // "general" first in the first category, so Guild.Create's system-channel pick (first Text
        // channel by category then position) still lands somewhere sensible.
        Add(home, "general", ChannelType.Text, "Everything that isn't a list or a chore", 0);
        Add(home, "groceries", ChannelType.List, "The shopping list", 1);
        Add(home, "chores", ChannelType.Chores, "Who does what, and when", 2);

        Add(house, "pantry", ChannelType.Pantry, "What's in the kitchen", 0);
        Add(house, "ledger", ChannelType.Ledger, "Shared expenses", 1);
        Add(house, "decisions", ChannelType.Decisions, "Things the house needs to agree on", 2);

        Add(voice, "house", ChannelType.Voice, "Voice channel", 0);

        return new List<Category> { home, house, voice };
    }
}