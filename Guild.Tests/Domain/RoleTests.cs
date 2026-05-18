using Guild.Domain.Aggregates;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

[TestFixture]
public class RoleTests
{
    [Test]
    public void Create_SetsPermissionsFromParams()
    {
        var role = Role.Create(new CreateRoleParams
        {
            Name = "Moderator",
            GuildId = "guild-1",
            Permissions = Permissions.ManageChannel | Permissions.ManagePermissions,
        });

        Assert.That(role.Permissions,
            Is.EqualTo(Permissions.ManageChannel | Permissions.ManagePermissions));
    }

    [Test]
    public void Create_DefaultsToNoPermissions_WhenNotProvided()
    {
        var role = Role.Create(new CreateRoleParams
        {
            Name = "Member",
            GuildId = "guild-1",
        });

        Assert.That(role.Permissions, Is.EqualTo(Permissions.None));
    }

    [Test]
    public void Create_PropagatesAllParams()
    {
        var role = Role.Create(new CreateRoleParams
        {
            Name = "Admin",
            Description = "Admin role",
            Color = "#FF0000",
            GuildId = "guild-1",
            Type = RoleType.None,
            Permissions = Permissions.Superadmin,
        });

        Assert.Multiple(() =>
        {
            Assert.That(role.Name, Is.EqualTo("Admin"));
            Assert.That(role.Description, Is.EqualTo("Admin role"));
            Assert.That(role.Color, Is.EqualTo("#FF0000"));
            Assert.That(role.GuildId, Is.EqualTo("guild-1"));
            Assert.That(role.Type, Is.EqualTo(RoleType.None));
            Assert.That(role.Permissions, Is.EqualTo(Permissions.Superadmin));
        });
    }
}
