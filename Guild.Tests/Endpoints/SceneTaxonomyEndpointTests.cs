using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The archive's shelves and labels: the depth cap, that removing a shelf never removes what is on
/// it, and the split where filing needs ManageScenes but tagging does not.
/// </summary>
[TestFixture]
public class SceneTaxonomyEndpointTests
{
    private const string GuildId = "guild-1";
    private const string SceneId = "chan-scene";
    private const string UserId = "user-1";
    private const string MemberId = "member-1";
    private const string RoleId = "role-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissions = null!;
    private AuditLogService _auditLog = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrate = null!;
    private SceneTaxonomyEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _hub = new FakeHubContext();
        _hydrate = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new SceneTaxonomyEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync(
        ModulePermissions module = ModulePermissions.ManageScenes,
        Permissions permissions = Permissions.ViewChannel)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Blackwater", Features = GuildFeatures.Scenes,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "Players",
            Permissions = permissions, ModulePermissions = module,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = SceneId, GuildId = GuildId, Name = "The Siege of Blackwater", Type = ChannelType.Scene,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.SceneStates.Add(SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = SceneId, GuildId = GuildId,
        }));

        await _context.SaveChangesAsync();
    }

    private async Task<SceneFolder> SeedFolderAsync(string name, string? parentId = null, int position = 0)
    {
        var folder = SceneFolder.Create(new CreateSceneFolderParams
        {
            GuildId = GuildId, Name = name, ParentFolderId = parentId, Position = position,
        });
        _context.SceneFolders.Add(folder);
        await _context.SaveChangesAsync();
        return folder;
    }

    private async Task<SceneTag> SeedTagAsync(string name, bool moderated = false, int position = 0)
    {
        var tag = SceneTag.Create(new CreateSceneTagParams
        {
            GuildId = GuildId, Name = name, Moderated = moderated, Position = position,
        });
        _context.SceneTags.Add(tag);
        await _context.SaveChangesAsync();
        return tag;
    }

    /// <summary>Reads the `error` off a Fault, which is Results.Json over an anonymous type.</summary>
    private static string? FaultCode(IResult result)
    {
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    // ══════════════════════════════════════════════════════════════════════ Folders
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateFolder_WithoutManageScenes_IsForbidden()
    {
        await SeedAsync(module: ModulePermissions.None);

        var result = await _endpoint.CreateFolderAsync(GuildId, new CreateSceneFolderDto { Name = "Arc I" },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateFolder_ThreeDeep_IsRefused()
    {
        await SeedAsync();
        var root = await SeedFolderAsync("Arc I");
        var child = await SeedFolderAsync("Sidequests", root.Id);

        var result = await _endpoint.CreateFolderAsync(GuildId,
            new CreateSceneFolderDto { Name = "Too deep", ParentFolderId = child.Id },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.EqualTo("scene_folder_depth_exceeded"));
    }

    [Test]
    public async Task CreateFolder_TwoDeep_IsAllowed()
    {
        await SeedAsync();
        var root = await SeedFolderAsync("Arc I");

        var result = await _endpoint.CreateFolderAsync(GuildId,
            new CreateSceneFolderDto { Name = "Sidequests", ParentFolderId = root.Id },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        var ok = result as Ok<SceneFolderDto>;
        Assert.That(ok!.Value!.ParentFolderId, Is.EqualTo(root.Id));
    }

    [Test]
    public async Task UpdateFolder_IntoItself_IsRefused()
    {
        await SeedAsync();
        var folder = await SeedFolderAsync("Arc I");

        var result = await _endpoint.UpdateFolderAsync(folder.Id,
            new UpdateSceneFolderDto { ParentFolderId = folder.Id },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.EqualTo("scene_folder_cycle"));
    }

    [Test]
    public async Task UpdateFolder_MovingAParentUnderAnother_IsRefused()
    {
        await SeedAsync();
        var first = await SeedFolderAsync("Arc I");
        await SeedFolderAsync("Sidequests", first.Id);
        var second = await SeedFolderAsync("Arc II");

        var result = await _endpoint.UpdateFolderAsync(first.Id,
            new UpdateSceneFolderDto { ParentFolderId = second.Id },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.EqualTo("scene_folder_depth_exceeded"));
    }

    [Test]
    public async Task UpdateFolder_EmptyParent_MovesItToTheRoot()
    {
        await SeedAsync();
        var root = await SeedFolderAsync("Arc I");
        var child = await SeedFolderAsync("Sidequests", root.Id);

        await _endpoint.UpdateFolderAsync(child.Id, new UpdateSceneFolderDto { ParentFolderId = "" },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(_context.SceneFolders.Single(f => f.Id == child.Id).ParentFolderId, Is.Null);
    }

    [Test]
    public async Task DeleteFolder_UnfilesItsScenesAndReparentsItsChildren()
    {
        await SeedAsync();
        var root = await SeedFolderAsync("Arc I");
        var child = await SeedFolderAsync("Sidequests", root.Id);
        _context.SceneStates.Single(s => s.ChannelId == SceneId).FolderId = root.Id;
        await _context.SaveChangesAsync();

        await _endpoint.DeleteFolderAsync(root.Id,
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.SceneFolders.Single(f => f.Id == child.Id).ParentFolderId, Is.Null);
            Assert.That(_context.SceneStates.Single(s => s.ChannelId == SceneId).FolderId, Is.Null);
            Assert.That(_context.Channels.Any(c => c.Id == SceneId), Is.True,
                "deleting a shelf is never a scene delete");
        });
    }

    [Test]
    public async Task ReorderFolders_RejectsAPartialList()
    {
        await SeedAsync();
        var first = await SeedFolderAsync("Arc I");
        await SeedFolderAsync("Arc II", position: 1);

        var result = await _endpoint.ReorderFoldersAsync(GuildId,
            new ReorderSceneFoldersDto { FolderIds = [first.Id] },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════ Tags
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateTag_WithATakenName_Conflicts()
    {
        await SeedAsync();
        await SeedTagAsync("betrayal");

        var result = await _endpoint.CreateTagAsync(GuildId, new CreateSceneTagDto { Name = "Betrayal" },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.EqualTo("scene_tag_name_taken"));
    }

    [Test]
    public async Task SetSceneTags_RefusesASixth()
    {
        await SeedAsync();
        var tags = new List<string>();
        for (var i = 0; i < SceneTag.MaxTagsPerScene + 1; i++)
            tags.Add((await SeedTagAsync($"tag-{i}", position: i)).Id);

        var result = await _endpoint.SetSceneTagsAsync(GuildId, SceneId, new SetSceneTagsDto { TagIds = tags },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.EqualTo("scene_tag_limit"));
    }

    [Test]
    public async Task SetSceneTags_OrdinaryMemberMayApplyAnOrdinaryTag()
    {
        await SeedAsync(module: ModulePermissions.None);
        var tag = await SeedTagAsync("betrayal");

        var result = await _endpoint.SetSceneTagsAsync(GuildId, SceneId,
            new SetSceneTagsDto { TagIds = [tag.Id] },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(FaultCode(result), Is.Null);
            Assert.That(_context.SceneTagAssignments.Count(a => a.SceneChannelId == SceneId), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SetSceneTags_OrdinaryMemberMayNotApplyAModeratedTag()
    {
        await SeedAsync(module: ModulePermissions.None);
        var tag = await SeedTagAsync("canon", moderated: true);

        var result = await _endpoint.SetSceneTagsAsync(GuildId, SceneId,
            new SetSceneTagsDto { TagIds = [tag.Id] },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.EqualTo("scene_tag_moderated"));
    }

    [Test]
    public async Task SetSceneTags_ManageScenesMayApplyAModeratedTag()
    {
        await SeedAsync();
        var tag = await SeedTagAsync("canon", moderated: true);

        var result = await _endpoint.SetSceneTagsAsync(GuildId, SceneId,
            new SetSceneTagsDto { TagIds = [tag.Id] },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.Null);
    }

    [Test]
    public async Task SetSceneTags_LeavesAModeratedTagAloneWhenTheEditDoesNotTouchIt()
    {
        await SeedAsync(module: ModulePermissions.None);
        var moderated = await SeedTagAsync("canon", moderated: true);
        var ordinary = await SeedTagAsync("ashfall", position: 1);
        _context.SceneTagAssignments.Add(new SceneTagAssignment
        {
            SceneChannelId = SceneId, TagId = moderated.Id, CreatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        // The moderated tag stays exactly where it was, so it is not in the delta and does not gate.
        var result = await _endpoint.SetSceneTagsAsync(GuildId, SceneId,
            new SetSceneTagsDto { TagIds = [moderated.Id, ordinary.Id] },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.Null);
    }

    [Test]
    public async Task SetSceneTags_RemovingAModeratedTagStillGates()
    {
        await SeedAsync(module: ModulePermissions.None);
        var moderated = await SeedTagAsync("canon", moderated: true);
        _context.SceneTagAssignments.Add(new SceneTagAssignment
        {
            SceneChannelId = SceneId, TagId = moderated.Id, CreatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetSceneTagsAsync(GuildId, SceneId, new SetSceneTagsDto { TagIds = [] },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(FaultCode(result), Is.EqualTo("scene_tag_moderated"));
    }

    [Test]
    public async Task SetSceneTags_WithoutViewChannel_IsForbidden()
    {
        await SeedAsync(module: ModulePermissions.None, permissions: Permissions.None);
        var tag = await SeedTagAsync("betrayal");

        var result = await _endpoint.SetSceneTagsAsync(GuildId, SceneId,
            new SetSceneTagsDto { TagIds = [tag.Id] },
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteTag_TakesItsApplicationsWithIt()
    {
        await SeedAsync();
        var tag = await SeedTagAsync("betrayal");
        _context.SceneTagAssignments.Add(new SceneTagAssignment
        {
            SceneChannelId = SceneId, TagId = tag.Id, CreatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        await _endpoint.DeleteTagAsync(tag.Id,
            _permissions, _context, _auditLog, _hub, _hydrate, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(_context.SceneTagAssignments.Any(a => a.TagId == tag.Id), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ Read
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTaxonomy_ReturnsBothHalvesInPositionOrder()
    {
        await SeedAsync(module: ModulePermissions.None);
        await SeedFolderAsync("Arc II", position: 1);
        await SeedFolderAsync("Arc I");
        await SeedTagAsync("betrayal", position: 1);
        await SeedTagAsync("ashfall");

        var result = await _endpoint.GetTaxonomyAsync(GuildId, _permissions, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<SceneTaxonomyDto>;
        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!.Folders.Select(f => f.Name), Is.EqualTo(new[] { "Arc I", "Arc II" }));
            Assert.That(ok.Value.Tags.Select(t => t.Name), Is.EqualTo(new[] { "ashfall", "betrayal" }));
        });
    }

    [Test]
    public async Task GetTaxonomy_WithoutTheScenesModule_IsForbidden()
    {
        await SeedAsync();
        var guild = _context.Guilds.Single(g => g.Id == GuildId);
        guild.Features = GuildFeatures.None;
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetTaxonomyAsync(GuildId, _permissions, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }
}
