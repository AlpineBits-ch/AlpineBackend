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
            Permissions =
                Enums.Permissions.ViewChannel |
                Enums.Permissions.SendMessages |
                Enums.Permissions.AddReactions |
                Enums.Permissions.AttachFiles |
                Enums.Permissions.EmbedLinks |
                Enums.Permissions.CreateThreads |
                Enums.Permissions.SendMessagesInThreads |
                Enums.Permissions.Connect |
                Enums.Permissions.Speak |
                Enums.Permissions.CreateInvite |
                Enums.Permissions.ChangeNickname |
                Enums.Permissions.Stream
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