using Guild.Contracts.Bus.Commands;
using Import.Application.Discord;
using Import.Application.Gateway;
using Import.Application.Mapping;
using Import.Domain.Entity;
using Import.Domain.Enums;
using Import.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Import.Tests.Gateway;

[TestFixture]
public class DiscordStructureSyncHandlerTests
{
    private const string EchoGuildId = "gild-1";
    private const string DiscordGuildId = "discord-guild-1";

    private string _dbName = null!;
    private TestImportContext _context = null!;
    private FakeSyncMessageBus _bus = null!;
    private DiscordStructureSyncHandler _handler = null!;
    private GuildLink _link = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestImportContext(_dbName);
        _bus = new FakeSyncMessageBus();
        _handler = new DiscordStructureSyncHandler(_context, _bus, NullLogger<DiscordStructureSyncHandler>.Instance);

        _link = new GuildLink
        {
            Id = GuildLink.GenerateId(),
            EchoGuildId = EchoGuildId,
            DiscordGuildId = DiscordGuildId,
            SyncDirection = SyncDirection.DiscordToVenta,
            Status = GuildLinkStatus.Active,
        };
        _context.GuildLinks.Add(_link);
        _context.SaveChanges();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DiscordChannelPayload MakeChannel(string id, int type = DiscordChannelTypeMapper.GuildText, string? parentId = null) => new()
    {
        Id = id,
        GuildId = DiscordGuildId,
        Type = type,
        Name = "general",
        Position = 0,
        ParentId = parentId,
    };

    [Test]
    public async Task HandleChannelUpsert_NoLinkForGuild_DoesNothing()
    {
        var unlinkedChannel = MakeChannel("chan-1");
        unlinkedChannel.GuildId = "some-other-discord-guild";

        await _handler.HandleChannelUpsertAsync(unlinkedChannel, CancellationToken.None);

        Assert.That(_bus.Invoked, Is.Empty);
    }

    [Test]
    public async Task HandleChannelUpsert_NoExistingMapping_CreatesAndRecordsMapping()
    {
        await _handler.HandleChannelUpsertAsync(MakeChannel("chan-1"), CancellationToken.None);

        Assert.That(_bus.Invoked, Has.Count.EqualTo(1));
        var command = (UpsertChannelFromSyncCommand)_bus.Invoked[0];
        Assert.That(command.EchoChannelId, Is.Null, "first sighting of this Discord channel must be a create, not an update");

        var mapping = _context.ImportEntityMappings.Single(m => m.DiscordId == "chan-1");
        Assert.That(mapping.EntityType, Is.EqualTo(ImportEntityType.Channel));
    }

    [Test]
    public async Task HandleChannelUpsert_ExistingMapping_IsTreatedAsUpdate()
    {
        await _handler.HandleChannelUpsertAsync(MakeChannel("chan-1"), CancellationToken.None);
        _bus.Invoked.Clear();

        await _handler.HandleChannelUpsertAsync(MakeChannel("chan-1"), CancellationToken.None);

        Assert.That(_bus.Invoked, Has.Count.EqualTo(1));
        var command = (UpsertChannelFromSyncCommand)_bus.Invoked[0];
        Assert.That(command.EchoChannelId, Is.Not.Null.And.EqualTo("echo-chan-1"),
            "second sighting must resolve the already-recorded Echo id and update, not duplicate-create");

        Assert.That(_context.ImportEntityMappings.Count(m => m.DiscordId == "chan-1"), Is.EqualTo(1));
    }

    [Test]
    public async Task HandleChannelUpsert_CategoryType_SetsIsCategoryTrue()
    {
        await _handler.HandleChannelUpsertAsync(MakeChannel("cat-1", type: DiscordChannelTypeMapper.GuildCategory), CancellationToken.None);

        var command = (UpsertChannelFromSyncCommand)_bus.Invoked[0];
        Assert.Multiple(() =>
        {
            Assert.That(command.IsCategory, Is.True);
            Assert.That(_context.ImportEntityMappings.Single(m => m.DiscordId == "cat-1").EntityType, Is.EqualTo(ImportEntityType.Category));
        });
    }

    [Test]
    public async Task HandleChannelUpsert_ParentCategoryAlreadyMapped_ResolvesEchoParentId()
    {
        await _handler.HandleChannelUpsertAsync(MakeChannel("cat-1", type: DiscordChannelTypeMapper.GuildCategory), CancellationToken.None);
        _bus.Invoked.Clear();

        await _handler.HandleChannelUpsertAsync(MakeChannel("chan-1", parentId: "cat-1"), CancellationToken.None);

        var command = (UpsertChannelFromSyncCommand)_bus.Invoked[0];
        Assert.That(command.EchoParentCategoryId, Is.EqualTo("echo-chan-1"));
    }

    [Test]
    public async Task HandleChannelUpsert_ThreadType_IsIgnored()
    {
        await _handler.HandleChannelUpsertAsync(MakeChannel("thread-1", type: DiscordChannelTypeMapper.GuildPublicThread), CancellationToken.None);

        Assert.That(_bus.Invoked, Is.Empty);
    }

    [Test]
    public async Task HandleChannelDelete_KnownMapping_DeletesAndRemovesMapping()
    {
        await _handler.HandleChannelUpsertAsync(MakeChannel("chan-1"), CancellationToken.None);
        _bus.Invoked.Clear();

        await _handler.HandleChannelDeleteAsync(MakeChannel("chan-1"), CancellationToken.None);

        Assert.That(_bus.Invoked, Has.Count.EqualTo(1));
        Assert.That(_bus.Invoked[0], Is.InstanceOf<DeleteChannelFromSyncCommand>());
        Assert.That(_context.ImportEntityMappings.Any(m => m.DiscordId == "chan-1"), Is.False);
    }

    [Test]
    public async Task HandleChannelDelete_UnknownMapping_DoesNothing()
    {
        await _handler.HandleChannelDeleteAsync(MakeChannel("never-seen"), CancellationToken.None);

        Assert.That(_bus.Invoked, Is.Empty);
    }

    private static DiscordRolePayload MakeRole(string id, string name = "Moderator") => new()
    {
        Id = id,
        Name = name,
        Color = 0xFF0000,
        Position = 1,
        Permissions = "8",
    };

    [Test]
    public async Task HandleRoleUpsert_EveryoneRole_DetectedByIdMatchingGuildId()
    {
        await _handler.HandleRoleUpsertAsync(DiscordGuildId, MakeRole(DiscordGuildId, "@everyone"), CancellationToken.None);

        var command = (UpsertRoleFromSyncCommand)_bus.Invoked[0];
        Assert.That(command.IsEveryoneRole, Is.True);
    }

    [Test]
    public async Task HandleRoleUpsert_NoExistingMapping_CreatesAndRecordsMapping()
    {
        await _handler.HandleRoleUpsertAsync(DiscordGuildId, MakeRole("role-mod"), CancellationToken.None);

        var command = (UpsertRoleFromSyncCommand)_bus.Invoked[0];
        Assert.Multiple(() =>
        {
            Assert.That(command.EchoRoleId, Is.Null);
            Assert.That(_context.ImportEntityMappings.Single(m => m.DiscordId == "role-mod").EntityType, Is.EqualTo(ImportEntityType.Role));
        });
    }

    [Test]
    public async Task HandleRoleUpsert_CarriesRoleMetadataSoLiveEditsDoNotDrift()
    {
        var role = MakeRole("role-bot", "MusicBot");
        role.Managed = true;
        role.Hoist = true;
        role.Mentionable = false;
        role.UnicodeEmoji = "🤖";
        role.Tags = new DiscordRoleTagsPayload { BotId = "1234567890" };

        await _handler.HandleRoleUpsertAsync(DiscordGuildId, role, CancellationToken.None);

        var command = (UpsertRoleFromSyncCommand)_bus.Invoked[0];
        Assert.Multiple(() =>
        {
            Assert.That(command.IsManaged, Is.True);
            Assert.That(command.BotUserId, Is.EqualTo("1234567890"));
            Assert.That(command.IntegrationId, Is.Null);
            Assert.That(command.Hoist, Is.True);
            Assert.That(command.Mentionable, Is.False);
            Assert.That(command.UnicodeEmoji, Is.EqualTo("🤖"));
            Assert.That(command.IconUrl, Is.Null);
        });
    }

    [Test]
    public async Task HandleRoleUpsert_RoleWithAnIcon_SendsTheCdnUrlAndDropsTheEmoji()
    {
        var role = MakeRole("role-vip", "VIP");
        role.Icon = "abc123";
        role.UnicodeEmoji = "⭐";

        await _handler.HandleRoleUpsertAsync(DiscordGuildId, role, CancellationToken.None);

        var command = (UpsertRoleFromSyncCommand)_bus.Invoked[0];
        Assert.Multiple(() =>
        {
            Assert.That(command.IconUrl, Is.EqualTo("https://cdn.discordapp.com/role-icons/role-vip/abc123.png"));
            Assert.That(command.UnicodeEmoji, Is.Null);
        });
    }

    [Test]
    public async Task HandleRoleDelete_KnownMapping_DeletesAndRemovesMapping()
    {
        await _handler.HandleRoleUpsertAsync(DiscordGuildId, MakeRole("role-mod"), CancellationToken.None);
        _bus.Invoked.Clear();

        await _handler.HandleRoleDeleteAsync(DiscordGuildId, "role-mod", CancellationToken.None);

        Assert.That(_bus.Invoked[0], Is.InstanceOf<DeleteRoleFromSyncCommand>());
        Assert.That(_context.ImportEntityMappings.Any(m => m.DiscordId == "role-mod"), Is.False);
    }

    [Test]
    public async Task HandleRoleUpsert_LinkPaused_DoesNothing()
    {
        _link.Status = GuildLinkStatus.Paused;
        await _context.SaveChangesAsync();

        await _handler.HandleRoleUpsertAsync(DiscordGuildId, MakeRole("role-mod"), CancellationToken.None);

        Assert.That(_bus.Invoked, Is.Empty);
    }
}
