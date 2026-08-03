using System.Security.Claims;
using Messaging.Application.Controllers;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using AttachmentDto = global::Messaging.Application.Dtos.Response.AttachmentDto;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Covers AttachmentController's non-S3 paths: attachment metadata lookup, the not-found guards
/// on download/thumbnail (which return before ever touching IAmazonS3), and the cache-hit fast
/// path on download/thumbnail (which also never touches S3). The actual S3 GetObjectAsync round
/// trip on a cache miss is intentionally left untested here - faking the full IAmazonS3 SDK
/// surface (dozens of members) isn't worth it for what's fundamentally a passthrough; s3Client is
/// passed null! in every test since none of them reach a code path that dereferences it.
/// This is a plain ASP.NET Core MVC controller ([ApiController], : ControllerBase, no
/// [WolverineHttp] attributes), so its explicit context.SaveChangesAsync() call is correct per
/// this repo's Wolverine-transaction convention, not a bug.
/// </summary>
[TestFixture]
public class AttachmentControllerTests
{
    private TestMessagingContext _context = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private AttachmentController MakeController(ClaimsPrincipal? user = null, FakeMessageBus? bus = null)
    {
        var controller = new AttachmentController(
            new FileService(null!), bus ?? new FakeMessageBus(), null!, _context, _cache,
            new ConversationPermissionService(_context, _cache));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user ?? TestPrincipal.ForUser("user-1") },
        };
        return controller;
    }

    private static Attachment MakeAttachment(string id, string? thumbnailId = null) => Attachment.Create(new CreateAttachmentParams
    {
        Id = id,
        FileName = "photo.png",
        ContentType = "image/png",
        SizeBytes = 1024,
        Url = "https://cdn/x",
        CreatorId = "user-1",
    }) is var a && thumbnailId is not null ? SetThumbnail(a, thumbnailId) : a;

    private static Attachment SetThumbnail(Attachment a, string thumbnailId)
    {
        a.ThumbnailId = thumbnailId;
        return a;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // UploadFileAsync - guard clauses only (no S3 interaction)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UploadFile_Unauthenticated_ReturnsBadRequest()
    {
        var controller = MakeController(TestPrincipal.Anonymous());

        var result = await controller.UploadFileAsync(new List<Microsoft.AspNetCore.Http.IFormFile>());

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetAttachmentAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetAttachment_DoesNotExist_ReturnsNotFound()
    {
        var controller = MakeController();

        var result = await controller.GetAttachmentAsync("nope");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetAttachment_Exists_ReturnsDtoWithRewrittenUrls()
    {
        _context.Attachments.Add(MakeAttachment("atac-1"));
        await _context.SaveChangesAsync();

        var controller = MakeController();

        var result = await controller.GetAttachmentAsync("atac-1");

        var ok = (OkObjectResult)result;
        var dto = (AttachmentDto)ok.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(dto.Url, Does.Contain("/api/v1/messaging/attachments/atac-1/download"));
            Assert.That(dto.ThumbnailUrl, Does.Contain("/api/v1/messaging/attachments/atac-1/thumbnail"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DownloadAttachmentAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Download_AttachmentDoesNotExist_ReturnsNotFound()
    {
        var controller = MakeController();

        var result = await controller.DownloadAttachmentAsync("nope");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Download_CacheHit_ReturnsCachedBytes_WithoutTouchingS3()
    {
        _context.Attachments.Add(MakeAttachment("atac-1"));
        await _context.SaveChangesAsync();
        await _cache.SetAsync(Attachment.GetCacheId("atac-1"), "cached-bytes"u8.ToArray(), new());

        var controller = MakeController();

        // s3Client is null! on this controller - if the cache-hit branch tried to fall through to
        // S3 this would NullReferenceException instead of returning cleanly.
        var result = await controller.DownloadAttachmentAsync("atac-1");

        var file = (FileContentResult)result;
        Assert.Multiple(() =>
        {
            Assert.That(file.ContentType, Is.EqualTo("image/png"));
            Assert.That(file.FileContents, Is.EqualTo("cached-bytes"u8.ToArray()));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetThumbnailAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Thumbnail_AttachmentDoesNotExist_ReturnsNotFound()
    {
        var controller = MakeController();

        var result = await controller.GetThumbnailAsync("nope");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Thumbnail_AttachmentHasNoThumbnailId_ReturnsNotFound()
    {
        _context.Attachments.Add(MakeAttachment("atac-1"));
        await _context.SaveChangesAsync();

        var controller = MakeController();

        var result = await controller.GetThumbnailAsync("atac-1");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Thumbnail_CacheHit_ReturnsCachedBytes_WithoutTouchingS3()
    {
        _context.Attachments.Add(MakeAttachment("atac-1", thumbnailId: "thumb-1"));
        await _context.SaveChangesAsync();
        await _cache.SetAsync(MinimalAttachment.GetCacheId("atac-1"), "thumb-bytes"u8.ToArray(), new());

        var controller = MakeController();

        var result = await controller.GetThumbnailAsync("atac-1");

        var file = (FileContentResult)result;
        Assert.Multiple(() =>
        {
            Assert.That(file.ContentType, Is.EqualTo("image/jpeg"));
            Assert.That(file.FileContents, Is.EqualTo("thumb-bytes"u8.ToArray()));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DeleteAttachmentAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Delete_AlwaysReturnsOk()
    {
        var controller = MakeController();

        var result = await controller.DeleteAttachmentAsync("any-id");

        Assert.That(result, Is.InstanceOf<OkResult>());
    }
}
