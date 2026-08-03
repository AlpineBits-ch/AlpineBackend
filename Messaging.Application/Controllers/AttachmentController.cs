using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;
using Facet.Extensions;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Events;
using Messaging.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Messaging.Application.Controllers;

/// <summary>
/// [Authorize] is on the class, not on individual actions. It used to sit on the upload action
/// alone, and Messaging registers no fallback authorization policy, so every read route here was
/// reachable anonymously - any leaked attachment id was permanent, credential-free access to the
/// file, with no revocation path.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/attachments")]
public class AttachmentController(
    FileService fileService,
    IMessageBus messageBus,
    IAmazonS3 s3Client,
    MicroserviceContext context,
    IDistributedCache cache,
    ConversationPermissionService conversationPermissions) : ControllerBase
{
    /// <summary>
    /// Whether the caller may read this attachment.
    ///
    /// Authorized against the attachment's context - the channel or conversation of the message it
    /// was sent in - so it follows exactly the same rule as reading that message. The uploader can
    /// always read their own file, which also covers an upload that has not been attached to a
    /// message yet, and rows predating <see cref="Attachment.ContextId"/>.
    /// </summary>
    private async Task<bool> CanReadAsync(Attachment attachment)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return false;

        if (string.Equals(attachment.CreatorId, userId, StringComparison.Ordinal)) return true;

        // No context recorded: either an upload not yet attached to a message, or a row created
        // before ContextId existed. Authenticated access only.
        //
        // Deliberately not creator-only. Attachments are embedded on their message with no reverse
        // index, so ContextId cannot be backfilled from a SQL migration - refusing these would make
        // every image ever posted, in every channel, unreadable to everyone but its uploader. The
        // headline defect (these routes were reachable with no credential at all) is closed for
        // legacy rows too; per-context authorization applies from here forward. Backfilling by
        // walking message history would let this branch be tightened to creator-only.
        if (string.IsNullOrWhiteSpace(attachment.ContextId)) return true;

        // A context id is either a channel id or a conversation id; ask Guild first and fall back
        // to conversation membership.
        var permission = await messageBus.InvokeAsync<HasUserPermissionToChannelResponse>(
            new HasUserPermissionToChannelRequest
            {
                ChannelId = attachment.ContextId,
                UserId = userId,
                Permission = ExternalPermission.ViewChannel,
            });

        if (permission.IsAllowed) return true;

        return await conversationPermissions.HasPermission(userId, attachment.ContextId);
    }

    [HttpPost]
    public async Task<IActionResult> UploadFileAsync([FromForm] ICollection<IFormFile> files)
    {

        // TODO: Get the users file upload limit. For now 35 may suffice

       var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
       if(userId is null) return BadRequest();
        
        if (files.Any(file => file.Length > 1024 * 1024 * 35))
        {
            return BadRequest("A file is too large");
        }
        
        var uploadedFiles = await fileService.UploadFileAsync(files);


        foreach (var file in uploadedFiles)
        {
            var attachment = Attachment.Create(new CreateAttachmentParams()
            {
                Id = file.Id,
                Url = file.Url,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
                ThumbnailUrl = null,
                CreatorId = userId
            });
            context.Attachments.Add(attachment);
        }
        await context.SaveChangesAsync();


        foreach (var file in uploadedFiles)
        {
            
            await messageBus.SendAsync(new ProcessAttachment()
            {
                AttachmentId = file.Id,
                ContentType = file.ContentType,
            });
        }
        
        return Ok(uploadedFiles.Select(f =>
        {
            return new CreateAttachmentResponse()
            {
                AttachmentId = f.Id,
                FileName = f.FileName
            };
        }));
    }


    [HttpGet("{id}")]
    public async Task<IActionResult> GetAttachmentAsync(string id)
    {
        var attachment = context.Attachments.FirstOrDefault(a => a.Id == id);
        if (attachment is null) return NotFound();
        if (!await CanReadAsync(attachment)) return NotFound();

        var dto = attachment.ToFacet<Attachment, AttachmentDto>();
        dto.Url = $"{Env.GeneralConfiguration.InstanceUrl}/api/v1/messaging/attachments/{id}/download";
        dto.ThumbnailUrl = $"{Env.GeneralConfiguration.InstanceUrl}/api/v1/messaging/attachments/{id}/thumbnail";
        return Ok(dto);
    }

    [HttpGet("{id}/download")]
public async Task<IActionResult> DownloadAttachmentAsync(string id)
{
    var attachment = await context.Attachments.FindAsync(id);
    if (attachment is null) return NotFound();
    if (!await CanReadAsync(attachment)) return NotFound();

    var data = await cache.GetAsync(Attachment.GetCacheId(id));
    if (data is not null) 
        return File(data, attachment.ContentType ?? "application/octet-stream", attachment.FileName);

    var memoryStream = new MemoryStream();
    
    try
    {
        // Request the object from the S3-compatible provider
        var request = new GetObjectRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = attachment.Id
        };

        using var response = await s3Client.GetObjectAsync(request);
        // Copy the cloud network stream into your local memory stream
        await response.ResponseStream.CopyToAsync(memoryStream);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return NotFound();
    }

    memoryStream.Position = 0; 
    
    await cache.SetAsync(Attachment.GetCacheId(id), memoryStream.ToArray(), new DistributedCacheEntryOptions()
    {
        SlidingExpiration = TimeSpan.FromMinutes(10)
    });

    memoryStream.Position = 0;
    return File(memoryStream, attachment.ContentType ?? "application/octet-stream", attachment.FileName);
}

[HttpGet("{id}/thumbnail")]
public async Task<IActionResult> GetThumbnailAsync(string id)
{
    var attachment = await context.Attachments.FindAsync(id);
    if (attachment is null || string.IsNullOrEmpty(attachment.ThumbnailId)) return NotFound();
    if (!await CanReadAsync(attachment)) return NotFound();

    var data = await cache.GetAsync(MinimalAttachment.GetCacheId(id));
    if (data is not null) return File(data, "image/jpeg");

    var memoryStream = new MemoryStream();

    try
    {
        var request = new GetObjectRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = attachment.ThumbnailId
        };

        using var response = await s3Client.GetObjectAsync(request);
        await response.ResponseStream.CopyToAsync(memoryStream);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return NotFound();
    }

    memoryStream.Position = 0;
    await cache.SetAsync(MinimalAttachment.GetCacheId(id), memoryStream.ToArray(), new DistributedCacheEntryOptions()
    {
        SlidingExpiration = TimeSpan.FromMinutes(10)
    });

    memoryStream.Position = 0;
    return File(memoryStream, "image/jpeg");
}

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAttachmentAsync(string id)
    {
        return Ok();
    }
   
}