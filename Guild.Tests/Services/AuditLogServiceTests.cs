using System.Text.Json;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// Covers AuditLogService.Log - a thin helper that queues a GuildAuditLogEntry on the
/// change-tracker without saving itself (the entry is only persisted, per its own doc comment,
/// when the caller's own SaveChanges commits alongside whatever action it describes).
/// </summary>
[TestFixture]
public class AuditLogServiceTests
{
    private TestGuildContext _context = null!;
    private AuditLogService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _service = new AuditLogService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void Log_QueuesEntryOnChangeTracker_WithoutSaving()
    {
        _service.Log("guild-1", "user-1", AuditActionType.EmojiCreated, "target-1");

        // Nothing committed yet - only tracked as a pending Added entity.
        Assert.That(_context.Set<GuildAuditLogEntry>().Local, Has.Count.EqualTo(1));
        Assert.That(_context.Set<GuildAuditLogEntry>().ToList(), Is.Empty,
            "A query against the InMemory store must not see the entry until SaveChanges commits it");
    }

    [Test]
    public async Task Log_AfterSaveChanges_PersistsWithGivenFields()
    {
        _service.Log("guild-1", "user-1", AuditActionType.EmojiCreated, "target-1", new { Name = "pepe" });
        await _context.SaveChangesAsync();

        var entry = _context.Set<GuildAuditLogEntry>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.GuildId, Is.EqualTo("guild-1"));
            Assert.That(entry.ActorUserId, Is.EqualTo("user-1"));
            Assert.That(entry.ActionType, Is.EqualTo(AuditActionType.EmojiCreated));
            Assert.That(entry.TargetId, Is.EqualTo("target-1"));
            Assert.That(entry.Metadata, Is.Not.Null);
        });
    }

    [Test]
    public void Log_NullMetadata_LeavesMetadataFieldNull()
    {
        _service.Log("guild-1", "user-1", AuditActionType.EmojiDeleted);

        var entry = _context.Set<GuildAuditLogEntry>().Local.Single();
        Assert.That(entry.Metadata, Is.Null);
        Assert.That(entry.TargetId, Is.Null);
    }

    [Test]
    public async Task Log_MetadataObject_SerializedAsJson()
    {
        _service.Log("guild-1", "user-1", AuditActionType.AutoModConfigUpdated, metadata: new { Enabled = true, Count = 3 });
        await _context.SaveChangesAsync();

        var entry = _context.Set<GuildAuditLogEntry>().Single();
        using var doc = JsonDocument.Parse(entry.Metadata!);
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("Enabled").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("Count").GetInt32(), Is.EqualTo(3));
        });
    }

    [Test]
    public void Log_MultipleCalls_QueuesMultipleEntries()
    {
        _service.Log("guild-1", "user-1", AuditActionType.EmojiCreated);
        _service.Log("guild-1", "user-1", AuditActionType.EmojiDeleted);

        Assert.That(_context.Set<GuildAuditLogEntry>().Local, Has.Count.EqualTo(2));
    }
}
