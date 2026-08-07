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
}

public class Role : Aggregate<Role>, IPrefixedEntity
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public string Color { get; set; } = "#000000";
    public string GuildId { get; init; }
    public virtual Aggregates.Guild Guild { get; init; }
    public Permissions Permissions { get; set; } = Permissions.None;
    [NotMapped] public static string Prefix { get; } = "role";

    public int Position { get; set; } = 0;
    
    public ICollection<RoleMember> Members { get; set; } = new List<RoleMember>();
    
    public RoleType Type { get; init; } = RoleType.None;

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
        Permissions.ViewWiki |
        HouseholdEveryonePermissions;

    /// <summary>
    /// What an ordinary member of a shared household can do: participate in every module, moderate
    /// none of it.
    /// </summary>
    public const Permissions HouseholdEveryonePermissions =
        Permissions.AddListItems |
        Permissions.CheckOffListItems |
        Permissions.CompleteChores |
        Permissions.AddExpenses |
        Permissions.ManagePantry |
        Permissions.CreateDecisions |
        Permissions.VoteDecisions |
        Permissions.PlanMeals |
        Permissions.LogMaintenance;

    /// <summary>
    /// What the seeded "Flatmates" role adds on top of <see cref="HouseholdEveryonePermissions"/>:
    /// the asymmetric bits, the ones that let you change something that is somebody else's.
    /// </summary>
    public const Permissions FlatmatePermissions =
        Permissions.ManageLists |
        Permissions.ManageChores |
        Permissions.ManageLedger |
        Permissions.ManageGuests |
        Permissions.ManageMeals |
        Permissions.ManageMaintenance;

    /// <summary>
    /// The part of <see cref="DefaultEveryonePermissions"/> that an @everyone mask arriving from
    /// outside cannot express, and which is therefore OR'd back on by <see
    /// cref="ApplyExternalEveryonePermissions"/> rather than being silently lost.
    /// </summary>
    public const Permissions ExternalEveryoneBaseline =
        Permissions.EditOwnMessages |
        Permissions.DeleteOwnMessages |
        Permissions.ManageOwnThreads |
        Permissions.ViewWiki |
        HouseholdEveryonePermissions;

    /// <summary>
    /// Replaces this @everyone role's permissions with a mask captured somewhere else - a Discord
    /// import or a guild template - keeping <see cref="ExternalEveryoneBaseline"/> intact.
    /// </summary>
    public void ApplyExternalEveryonePermissions(Permissions external)
    {
        if (Type != RoleType.Everyone)
            throw new InvalidOperationException(
                $"Role {Id} is not the @everyone role; use Permissions directly for ordinary roles.");

        Permissions = external | ExternalEveryoneBaseline;
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
            Name = "Everyone",
            Members = [new RoleMember()
            {
                Id = RoleMember.GenerateId(),
                CreatedAt = date,
                UpdatedAt = date,
                RoleId = roleId,
                MemberId = memberId
            }],
            Permissions = DefaultEveryonePermissions,
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
            Permissions = FlatmatePermissions,
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
        };

        role.AddDomainEvent(new RoleCreated()
        {
            RoleId = id,
            GuildId = parameters.GuildId,
        });
        
        return role;
    }
}