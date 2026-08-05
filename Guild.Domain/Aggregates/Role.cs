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
        Permissions.ViewWiki;

    /// <summary>
    /// The part of <see cref="DefaultEveryonePermissions"/> that an @everyone mask arriving from
    /// outside cannot express, and which is therefore OR'd back on by <see
    /// cref="ApplyExternalEveryonePermissions"/> rather than being silently lost.
    /// </summary>
    public const Permissions ExternalEveryoneBaseline =
        Permissions.EditOwnMessages |
        Permissions.DeleteOwnMessages |
        Permissions.ManageOwnThreads |
        Permissions.ViewWiki;

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