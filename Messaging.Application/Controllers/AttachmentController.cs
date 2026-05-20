using System.Security.Claims;
using Facet.Extensions;
using Google.Cloud.Storage.V1;
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

[ApiController]
[Route("api/v1/attachments")]
public class AttachmentController(FileService fileService, IMessageBus messageBus, MicroserviceContext context, StorageClient storageClient, IDistributedCache cache) : ControllerBase
{
    [Authorize]

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
        var dto = attachment.ToFacet<Attachment, AttachmentDto>();
        
        dto.Url = $"https://api.alpinebits.ch/api/v1/messaging/attachments/{id}/download";
        dto.ThumbnailUrl = $"https://api.alpinebits.ch/api/v1/messaging/attachments/{id}/thumbnail";
        return Ok(dto);
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadAttachmentAsync(string id)
    {
        var attachment = await context.Attachments.FindAsync(id);
        if (attachment is null) return NotFound();
        var data = await cache.GetAsync(Attachment.GetCacheId(id));
        if(data is not null) return File(data, attachment.ContentType ?? "application/octet-stream", attachment.FileName);
        var memoryStream = new MemoryStream();
        await storageClient.DownloadObjectAsync("echo-chat", attachment.Id, memoryStream);
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
        
        var data = await cache.GetAsync(MinimalAttachment.GetCacheId(id));
        if(data is not null) return File(data, "image/jpeg");

        var memoryStream = new MemoryStream();
        await storageClient.DownloadObjectAsync("echo-chat", attachment.ThumbnailId, memoryStream);
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