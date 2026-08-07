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

    public GuildKind Kind { get; init; } = GuildKind.Community;

    /// <summary>Overrides the preset <see cref="Kind"/> would otherwise seed.</summary>
    public GuildFeatures? Features { get; init; }

    /// <summary>When true, skips seeding the default "Text Channels"/"Voice Channels"
    /// categories - used by Discord-import, which populates its own category/channel tree
    /// instead. The @everyone role and owner membership are still always created.</summary>
    public bool SkipDefaultChannels { get; init; } = false;
}

public class Guild : Aggregate<Guild>, IPrefixedEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    
    public string OwnerId { get; set; }
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

    /// <summary>Null for a locally-created guild.</summary>
    public string? OriginInstanceId { get; set; }

    public GuildVerificationLevel VerificationLevel { get; set; } = GuildVerificationLevel.None;

    /// <summary>
    /// The notification level a member falls back to when they have set nothing of their own - the
    /// bottom of the chain NotificationResolutionService walks.
    /// </summary>
    public NotificationLevel DefaultMessageNotifications { get; set; } = NotificationLevel.AllMessages;

    /// <summary>What this guild is - drives the client shell and seeds <see cref="Features"/>.
    /// Nothing here gates on it directly; see <see cref="GuildFeatures"/>.</summary>
    public GuildKind Kind { get; set; } = GuildKind.Community;

    /// <summary>The modules this guild actually has.</summary>
    public GuildFeatures Features { get; set; } = GuildFeaturePresets.Community;

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
            Kind = parameters.Kind,
            // An explicitly-replayed feature set wins; otherwise the kind's preset.
            Features = parameters.Features is null or GuildFeatures.None
                ? GuildFeaturePresets.For(parameters.Kind)
                : parameters.Features.Value,
            Categories = parameters.SkipDefaultChannels
                ? new List<Category>()
                : Category.GetDefaultForKind(parameters.Kind, id),
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
                OnboardingCompletedAt = date,
            }],
            // A Household additionally gets a Flatmates role holding the owner: it is the default
            // chore rotation pool, and it is what separates the people who live here from a guest
            // who joined by invite.
            Roles = parameters.Kind == GuildKind.Household
                ? [Role.CreateEveryoneRole(id, memberId), Role.CreateFlatmatesRole(id, memberId)]
                : [Role.CreateEveryoneRole(id, memberId)]
        };

        // When default channels are skipped (Discord import), there's no text channel yet to
        // point at - the importer sets SystemChannelId itself once it has created its own tree.
        var defaultTextChannel = guild.Categories
            .OrderBy(c => c.Position)
            .SelectMany(c => c.Channels)
            .FirstOrDefault(c => c.Type == ChannelType.Text);
        guild.SystemChannelId = defaultTextChannel?.Id;

        guild.AddDomainEvent(new GuildCreated() { GuildId = id });
        return guild;
    }

    /// <summary>
    /// The starter house manual a new household's wiki is seeded with, or null for every other kind
    /// of guild.
    /// </summary>
    public static Services.HouseManualSeed.HouseManual? HouseManualFor(Guild guild, CreateGuildParams parameters) =>
        guild.Kind == GuildKind.Household && !parameters.SkipDefaultChannels
            ? Services.HouseManualSeed.ForHousehold(guild.Id, guild.OwnerId)
            : null;
}