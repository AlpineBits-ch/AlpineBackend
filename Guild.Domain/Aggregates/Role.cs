using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Role;
using Persistence;

namespace Guild.Domain.Aggregates;

public class CreateRoleParams
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public string Color { get; set; } = "#000000";
    public string GuildId { get; set; }
    public RoleType Type { get; set; } = RoleType.None;
    public Permissions Permissions { get; set; } = Permissions.None;
    public ModulePermissions ModulePermissions { get; set; } = ModulePermissions.None;
    public bool Hoist { get; set; }
    public bool Mentionable { get; set; } = true;
    public string? IconUrl { get; set; }
    public string? UnicodeEmoji { get; set; }
}

public class Role : Aggregate<Role>, IPrefixedEntity
{
    private string _name;

    /// <summary>The role's display name.</summary>
    public string Name
    {
        get => _name;
        init => _name = value;
    }

    public string? Description { get; set; }
    public string Color { get; set; } = "#000000";
    public string GuildId { get; init; }
    public virtual Aggregates.Guild Guild { get; init; }
    public Permissions Permissions { get; set; } = Permissions.None;

    /// <summary>The <see cref="Enums.ModulePermissions"/> sibling of <see cref="Permissions"/>,
    /// carried separately because the two are separate 64-bit masks. See the remarks on
    /// <see cref="Enums.ModulePermissions"/> for why the split exists.</summary>
    public ModulePermissions ModulePermissions { get; set; } = ModulePermissions.None;

    [NotMapped] public static string Prefix { get; } = "role";

    public int Position { get; set; } = 0;

    /// <summary>
    /// Display this role's members in their own group in the member list, above the ungrouped
    /// members.
    /// </summary>
    public bool Hoist { get; set; }

    /// <summary>
    /// Whether members without <see cref="Permissions.MentionEveryone"/> may @mention this role.
    /// </summary>
    public bool Mentionable { get; set; } = true;

    /// <summary>An uploaded image shown beside the role's members, or null.</summary>
    public string? IconUrl { get; private set; }

    /// <summary>A single unicode emoji used as the role badge, or null.</summary>
    public string? UnicodeEmoji { get; private set; }

    /// <summary>True when an integration owns this role rather than a person: a bot install, or a
    /// future subscription/connection integration. Managed roles are not editable or deletable by
    /// humans - the integration would simply recreate or contradict the change - which
    /// <see cref="IsEditableByHumans"/> expresses and the role endpoints enforce.</summary>
    public bool IsManaged { get; set; }

    /// <summary>The bot user this role was created for, when <see cref="IsManaged"/> is set because
    /// of a bot install. Discord calls this the role's <c>bot_id</c> tag.</summary>
    public string? BotUserId { get; set; }

    /// <summary>The integration this role belongs to, when it is owned by something other than a
    /// bot user. Discord's <c>integration_id</c> tag.</summary>
    public string? IntegrationId { get; set; }

    public ICollection<RoleMember> Members { get; set; } = new List<RoleMember>();

    public RoleType Type { get; init; } = RoleType.None;

    /// <summary>The name Discord pins on its own @everyone role, and the one this aggregate
    /// refuses to let anybody change.</summary>
    public const string EveryoneRoleName = "Everyone";

    /// <summary>Renames the role, refusing for <see cref="RoleType.Everyone"/>.</summary>
    public void Rename(string name)
    {
        if (Type == RoleType.Everyone)
            throw new InvalidOperationException(
                $"Role {Id} is the @everyone role, whose name is fixed at \"{EveryoneRoleName}\".");

        _name = name;
    }

    /// <summary>Sets the role badge to an uploaded icon, a unicode emoji, or neither.</summary>
    public void SetBadge(string? iconUrl, string? unicodeEmoji)
    {
        var hasIcon = !string.IsNullOrWhiteSpace(iconUrl);
        var hasEmoji = !string.IsNullOrWhiteSpace(unicodeEmoji);

        if (hasIcon && hasEmoji)
            throw new InvalidOperationException(
                $"Role {Id} cannot carry both an icon and a unicode emoji; pass one or neither.");

        IconUrl = hasIcon ? iconUrl : null;
        UnicodeEmoji = hasEmoji ? unicodeEmoji : null;
    }

    /// <summary>False for a role an integration owns.</summary>
    public bool IsEditableByHumans => !IsManaged;

    /// <summary>
    /// The permission set every newly created guild grants its @everyone role, and the single
    /// source of truth for "what a plain member can do out of the box".
    /// </summary>
    public const Permissions DefaultEveryonePermissions =
        Permissions.ViewChannel |
        Permissions.SendMessages |
        Permissions.EditOwnMessages |
        Permissions.DeleteOwnMessages |
        Permissions.AddReactions |
        Permissions.AttachFiles |
        Permissions.EmbedLinks |
        Permissions.CreateThreads |
        Permissions.SendMessagesInThreads |
        Permissions.ManageOwnThreads |
        Permissions.Connect |
        Permissions.Speak |
        Permissions.Stream |
        Permissions.CreateInvite |
        Permissions.ChangeNickname |
        Permissions.ReadMessageHistory |
        Permissions.UseApplicationCommands |
        Permissions.UseExternalEmojis |
        Permissions.UseExternalStickers |
        Permissions.UseVoiceActivity |
        Permissions.SendPolls |
        Permissions.SendVoiceMessages;

    /// <summary>The <see cref="ModulePermissions"/> half of the @everyone grant.</summary>
    public const ModulePermissions DefaultEveryoneModulePermissions =
        ModulePermissions.ViewWiki |
        HouseholdEveryonePermissions;

    /// <summary>
    /// What an ordinary member of a shared household can do: participate in every module, moderate
    /// none of it.
    /// </summary>
    public const ModulePermissions HouseholdEveryonePermissions =
        ModulePermissions.AddListItems |
        ModulePermissions.CheckOffListItems |
        ModulePermissions.CompleteChores |
        ModulePermissions.AddExpenses |
        ModulePermissions.ManagePantry |
        ModulePermissions.CreateDecisions |
        ModulePermissions.VoteDecisions |
        ModulePermissions.PlanMeals |
        ModulePermissions.LogMaintenance;

    /// <summary>
    /// What the seeded "Flatmates" role adds on top of <see cref="HouseholdEveryonePermissions"/>:
    /// the asymmetric bits, the ones that let you change something that is somebody else's.
    /// </summary>
    public const ModulePermissions FlatmatePermissions =
        ModulePermissions.ManageLists |
        ModulePermissions.ManageChores |
        ModulePermissions.ManageLedger |
        ModulePermissions.ManageGuests |
        ModulePermissions.ManageMeals |
        ModulePermissions.ManageMaintenance;

    /// <summary>
    /// The part of <see cref="DefaultEveryonePermissions"/> that an @everyone mask arriving from
    /// outside cannot express, and which is therefore OR'd back on by <see
    /// cref="ApplyExternalEveryonePermissions"/> rather than being silently lost.
    /// </summary>
    public const Permissions ExternalEveryoneBaseline =
        Permissions.EditOwnMessages |
        Permissions.DeleteOwnMessages |
        Permissions.ManageOwnThreads;

    /// <summary>
    /// The <see cref="ModulePermissions"/> half of <see cref="ExternalEveryoneBaseline"/>.
    /// </summary>
    public const ModulePermissions ExternalEveryoneModuleBaseline =
        ModulePermissions.ViewWiki |
        HouseholdEveryonePermissions;

    /// <summary>
    /// Replaces this @everyone role's permissions with a mask captured somewhere else - a Discord
    /// import or a guild template - keeping <see cref="ExternalEveryoneBaseline"/> intact.
    /// </summary>
    public void ApplyExternalEveryonePermissions(
        Permissions external,
        ModulePermissions externalModule = ModulePermissions.None)
    {
        if (Type != RoleType.Everyone)
            throw new InvalidOperationException(
                $"Role {Id} is not the @everyone role; use Permissions directly for ordinary roles.");

        Permissions = external | ExternalEveryoneBaseline;
        ModulePermissions = externalModule | ExternalEveryoneModuleBaseline;
    }

    public static Role CreateEveryoneRole(string guildId, string memberId)
    {
        var roleId = GenerateId();

        var date = DateTime.UtcNow;
        var role = new Role()
        {
            Id = roleId,
            CreatedAt = date,
            UpdatedAt = date,
            Type = RoleType.Everyone,
            GuildId = guildId,
            Position = 0,
            Name = EveryoneRoleName,
            Members = [new RoleMember()
            {
                Id = RoleMember.GenerateId(),
                CreatedAt = date,
                UpdatedAt = date,
                RoleId = roleId,
                MemberId = memberId
            }],
            Permissions = DefaultEveryonePermissions,
            ModulePermissions = DefaultEveryoneModulePermissions,
        };

        return role;
    }
    
    /// <summary>The role a Household guild is seeded with, holding the owner.</summary>
    public static Role CreateFlatmatesRole(string guildId, string memberId)
    {
        var roleId = GenerateId();
        var date = DateTime.UtcNow;

        return new Role
        {
            Id = roleId,
            CreatedAt = date,
            UpdatedAt = date,
            Type = RoleType.None,
            GuildId = guildId,
            // Above @everyone (0), so a flatmate can hand out guest access and a guest can never
            // manage a flatmate. Nothing else is seeded, so there is no position to collide with.
            Position = 1,
            Name = FlatmatesRoleName,
            Color = "#4F8A6B",
            Description = "Everyone who actually lives here. Chores rotate over this role.",
            Members = [new RoleMember
            {
                Id = RoleMember.GenerateId(),
                CreatedAt = date,
                UpdatedAt = date,
                RoleId = roleId,
                MemberId = memberId,
            }],
            ModulePermissions = FlatmatePermissions,
        };
    }

    public const string FlatmatesRoleName = "Flatmates";

    public static Role Create(CreateRoleParams parameters)
    {
        var date = DateTime.UtcNow;
        var id = GenerateId();
        var role = new Role()
        {
            Id = id,
            CreatedAt = date,
            Name = parameters.Name,
            Description = parameters.Description,
            Color = parameters.Color,
            GuildId = parameters.GuildId,
            Type = parameters.Type,
            Permissions = parameters.Permissions,
            ModulePermissions = parameters.ModulePermissions,
            Hoist = parameters.Hoist,
            Mentionable = parameters.Mentionable,
        };

        role.SetBadge(parameters.IconUrl, parameters.UnicodeEmoji);

        role.AddDomainEvent(new RoleCreated()
        {
            RoleId = id,
            GuildId = parameters.GuildId,
        });
        
        return role;
    }
}