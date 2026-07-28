using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;

namespace Guild.Tests.Bus.Consumers;

/// <summary>Covers UpsertChannelFromSyncHandler/DeleteChannelFromSyncHandler - the granular,
/// one-event-at-a-time commands the Import service's live Discord Gateway sync uses after the
/// initial bulk import, as opposed to ImportGuildStructureHandler's one-shot bulk creation.</summary>
[TestFixture]
public class ChannelSyncHandlersTests
{
    private const string GuildId = "guild-1";
    private const string RoleId = "role-1";

    private string _dbName = null!;
    private TestGuildContext _context = null!;
    private AuditLogService _auditLog = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestGuildContext(_dbName);
        _auditLog = new AuditLogService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>UpsertChannelFromSyncHandler doesn't call SaveChangesAsync itself (bus handlers
    /// auto-commit via Wolverine's DbContext middleware in production, per this repo's
    /// convention) - tests invoke it directly with no such middleware present, so this simulates
    /// that one commit before asserting on query results (EF Core's InMemory provider doesn't
    /// reflect uncommitted Added/Modified entities in a plain LINQ query).</summary>
    private async Task<UpsertChannelFromSyncResponse> Upsert(UpsertChannelFromSyncCommand command)
    {
        var response = await UpsertChannelFromSyncHandler.Handle(command, _context, _auditLog);
        await _context.SaveChangesAsync();
        return response;
    }

    private async Task Delete(DeleteChannelFromSyncCommand command)
    {
        await DeleteChannelFromSyncHandler.Handle(command, _context, _auditLog);
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Upsert_NoEchoCategoryId_CreatesNewCategory()
    {
        var response = await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId,
            IsCategory = true,
            Name = "General",
            Position = 0,
        });

        var category = _context.Categories.Single(c => c.Id == response.EchoId);
        Assert.That(category.Name, Is.EqualTo("General"));
    }

    [Test]
    public async Task Upsert_WithEchoCategoryId_UpdatesExistingCategory()
    {
        var created = await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId, IsCategory = true, Name = "General", Position = 0,
        });

        var updated = await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId, IsCategory = true, EchoCategoryId = created.EchoId, Name = "Renamed", Position = 2,
        });

        Assert.That(updated.EchoId, Is.EqualTo(created.EchoId));
        var category = _context.Categories.Single(c => c.Id == created.EchoId);
        Assert.Multiple(() =>
        {
            Assert.That(category.Name, Is.EqualTo("Renamed"));
            Assert.That(category.Position, Is.EqualTo(2));
        });
        Assert.That(_context.Categories.Count(c => c.GuildId == GuildId), Is.EqualTo(1), "an update must not create a duplicate row");
    }

    [Test]
    public async Task Upsert_NoEchoChannelId_CreatesNewChannel()
    {
        var response = await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId,
            IsCategory = false,
            Name = "general",
            Type = "Text",
            Position = 0,
        });

        var channel = _context.Channels.Single(c => c.Id == response.EchoId);
        Assert.That(channel.Name, Is.EqualTo("general"));
    }

    [Test]
    public async Task Upsert_ChannelWithOverwrite_CreatesChannelPermissionRow()
    {
        _context.Roles.Add(new Guild.Domain.Aggregates.Role
        {
            Id = RoleId, GuildId = GuildId, Name = "Mod",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var response = await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId, IsCategory = false, Name = "general", Type = "Text", Position = 0,
            Overwrites = [new SyncOverwriteDto { EchoRoleId = RoleId, AllowPermissions = 1, DenyPermissions = 0 }],
        });

        var overwrite = _context.Set<ChannelPermission>().Single(p => p.ChannelId == response.EchoId);
        Assert.That(overwrite.RoleId, Is.EqualTo(RoleId));
    }

    [Test]
    public async Task Upsert_UpdateReplacesOverwrites_DoesNotAccumulateStaleRows()
    {
        _context.Roles.Add(new Guild.Domain.Aggregates.Role
        {
            Id = RoleId, GuildId = GuildId, Name = "Mod",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var created = await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId, IsCategory = false, Name = "general", Type = "Text", Position = 0,
            Overwrites = [new SyncOverwriteDto { EchoRoleId = RoleId, AllowPermissions = 1, DenyPermissions = 0 }],
        });

        await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId, IsCategory = false, EchoChannelId = created.EchoId, Name = "general", Type = "Text", Position = 0,
            Overwrites = [], // Discord removed the overwrite
        });

        Assert.That(_context.Set<ChannelPermission>().Count(p => p.ChannelId == created.EchoId), Is.EqualTo(0));
    }

    [Test]
    public async Task Delete_ExistingChannel_RemovesRow()
    {
        var created = await Upsert(new UpsertChannelFromSyncCommand
        {
            GuildId = GuildId, IsCategory = false, Name = "general", Type = "Text", Position = 0,
        });

        await Delete(new DeleteChannelFromSyncCommand { GuildId = GuildId, EchoId = created.EchoId, IsCategory = false });

        Assert.That(_context.Channels.Any(c => c.Id == created.EchoId), Is.False);
    }

    [Test]
    public async Task Delete_UnknownChannel_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => Delete(
            new DeleteChannelFromSyncCommand { GuildId = GuildId, EchoId = "chan-does-not-exist", IsCategory = false }));
    }
}
