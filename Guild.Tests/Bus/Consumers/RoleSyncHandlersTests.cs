using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;

namespace Guild.Tests.Bus.Consumers;

/// <summary>Covers UpsertRoleFromSyncHandler/DeleteRoleFromSyncHandler - the granular sync
/// commands the Import service's live Discord Gateway sync uses for GUILD_ROLE_* dispatches.</summary>
[TestFixture]
public class RoleSyncHandlersTests
{
    private const string GuildId = "guild-1";
    private const string EveryoneRoleId = "role-everyone";

    private string _dbName = null!;
    private TestGuildContext _context = null!;
    private AuditLogService _auditLog = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestGuildContext(_dbName);
        _auditLog = new AuditLogService(_context);

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Type = RoleType.Everyone, Name = "Everyone",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.SaveChanges();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>UpsertRoleFromSyncHandler doesn't call SaveChangesAsync itself (bus handlers
    /// auto-commit via Wolverine's DbContext middleware in production) - tests must simulate
    /// that one commit, since EF Core's InMemory provider doesn't reflect uncommitted
    /// Added/Modified entities in a plain LINQ query.</summary>
    private async Task<UpsertRoleFromSyncResponse> Upsert(UpsertRoleFromSyncCommand command)
    {
        var response = await UpsertRoleFromSyncHandler.Handle(command, _context, _auditLog);
        await _context.SaveChangesAsync();
        return response;
    }

    private async Task Delete(DeleteRoleFromSyncCommand command)
    {
        await DeleteRoleFromSyncHandler.Handle(command, _context, _auditLog);
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Upsert_EveryoneRole_UpdatesExistingRoleInsteadOfCreating()
    {
        var response = await Upsert(new UpsertRoleFromSyncCommand
        {
            GuildId = GuildId, IsEveryoneRole = true, Name = "@everyone", Color = "#123456", Position = 0, Permissions = 0b11,
        });

        Assert.That(response.EchoId, Is.EqualTo(EveryoneRoleId));
        Assert.That(_context.Roles.Count(r => r.GuildId == GuildId), Is.EqualTo(1), "must not create a second @everyone row");

        var role = _context.Roles.Single(r => r.Id == EveryoneRoleId);
        Assert.That((ulong)role.Permissions, Is.EqualTo(0b11ul));
    }

    [Test]
    public async Task Upsert_NoEchoRoleId_CreatesNewRole()
    {
        var response = await Upsert(new UpsertRoleFromSyncCommand
        {
            GuildId = GuildId, Name = "Moderator", Color = "#FF0000", Position = 1, Permissions = 0b1000,
        });

        var role = _context.Roles.Single(r => r.Id == response.EchoId);
        Assert.That(role.Name, Is.EqualTo("Moderator"));
    }

    [Test]
    public async Task Upsert_WithEchoRoleId_UpdatesExistingRole()
    {
        var created = await Upsert(new UpsertRoleFromSyncCommand
        {
            GuildId = GuildId, Name = "Moderator", Color = "#FF0000", Position = 1, Permissions = 0b1000,
        });

        var updated = await Upsert(new UpsertRoleFromSyncCommand
        {
            GuildId = GuildId, EchoRoleId = created.EchoId, Name = "Renamed", Color = "#00FF00", Position = 2, Permissions = 0b1,
        });

        Assert.That(updated.EchoId, Is.EqualTo(created.EchoId));
        var role = _context.Roles.Single(r => r.Id == created.EchoId);
        Assert.Multiple(() =>
        {
            Assert.That(role.Name, Is.EqualTo("Renamed"));
            Assert.That((ulong)role.Permissions, Is.EqualTo(0b1ul));
        });
        Assert.That(_context.Roles.Count(r => r.GuildId == GuildId), Is.EqualTo(2), "everyone role + the one custom role, no duplicate");
    }

    [Test]
    public async Task Delete_ExistingRole_RemovesRow()
    {
        var created = await Upsert(new UpsertRoleFromSyncCommand
        {
            GuildId = GuildId, Name = "Moderator", Color = "#FF0000", Position = 1, Permissions = 0b1000,
        });

        await Delete(new DeleteRoleFromSyncCommand { GuildId = GuildId, EchoRoleId = created.EchoId });

        Assert.That(_context.Roles.Any(r => r.Id == created.EchoId), Is.False);
    }

    [Test]
    public async Task Delete_EveryoneRole_IsNeverRemoved()
    {
        await Delete(new DeleteRoleFromSyncCommand { GuildId = GuildId, EchoRoleId = EveryoneRoleId });

        Assert.That(_context.Roles.Any(r => r.Id == EveryoneRoleId), Is.True);
    }

    [Test]
    public async Task Delete_UnknownRole_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => Delete(
            new DeleteRoleFromSyncCommand { GuildId = GuildId, EchoRoleId = "role-does-not-exist" }));
    }
}
