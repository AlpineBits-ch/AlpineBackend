using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
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

/// <summary>[Authorize] is on the class, not on individual actions.</summary>
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
    /// <summary>Whether the caller may read this attachment.</summary>
    private async Task<bool> CanReadAsync(Attachment attachment)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return false;

        if (string.Equals(attachment.CreatorId, userId, StringComparison.Ordinal)) return true;

        // No context recorded: either an upload not yet attached to a message, or a row created
        // before ContextId existed.
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

    /// <summary>
    /// Stores attachments, subject to the storage entitlements enforced in <see
    /// cref="FileService"/>.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> UploadFileAsync(
        [FromForm] ICollection<IFormFile> files, [FromQuery] string? guildId = null)
    {
       var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
       if(userId is null) return BadRequest();

        var chargedGuildId = string.IsNullOrWhiteSpace(guildId) ? null : guildId.Trim();

        if (chargedGuildId is not null)
        {
            var attach = await messageBus.InvokeAsync<HasUserPermissionToGuildResponse>(
                new HasUserPermissionToGuildRequest
                {
                    GuildId = chargedGuildId,
                    UserId = userId,
                    Permission = ExternalPermission.AttachFiles,
                });

            if (!attach.IsAllowed) return Forbid();
        }

        var uploadContext = chargedGuildId is null
            ? StorageUploadContext.ForUser(userId)
            : StorageUploadContext.ForGuild(chargedGuildId, userId);

        var result = await fileService.UploadFileAsync(files, uploadContext, HttpContext.RequestAborted);

        var degradations = await DescribeAsync(result.Rejected, chargedGuildId, userId);

        if (result.Refused)
        {
            return StatusCode(EntitlementDenialDto.StatusCode, EntitlementDenialDto.From(degradations[0]));
        }

        var uploadedFiles = result.Uploaded;

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
        
        var stored = uploadedFiles.Select(f => new CreateAttachmentResponse()
        {
            AttachmentId = f.Id,
            FileName = f.FileName
        }).ToList();

        // The success body has always been a bare array, and degradations have to ride the body of
        // the action that caused them - there is no client interceptor that would find them
        // anywhere else.
        return degradations.Count == 0
            ? Ok(stored)
            : Ok(EntitlementResponses.WithDegradations(new { attachments = stored }, degradations));
    }

    /// <summary>
    /// The guild's storage consumption, for the meter that <c>storage.guild_quota_bytes</c> is
    /// otherwise unrenderable without.
    /// </summary>
    [HttpGet("usage")]
    public async Task<IActionResult> GetStorageUsageAsync([FromQuery] string guildId)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return BadRequest();
        if (string.IsNullOrWhiteSpace(guildId)) return BadRequest();

        var attach = await messageBus.InvokeAsync<HasUserPermissionToGuildResponse>(
            new HasUserPermissionToGuildRequest
            {
                GuildId = guildId.Trim(),
                UserId = userId,
                Permission = ExternalPermission.AttachFiles,
            });

        if (!attach.IsAllowed) return Forbid();

        return Ok(await fileService.UsageForGuildAsync(guildId.Trim(), HttpContext.RequestAborted));
    }

    /// <summary>Every rejected file as the client reads it.</summary>
    private async Task<IReadOnlyList<EntitlementDegradationDto>> DescribeAsync(
        IReadOnlyList<RejectedUpload> rejected, string? guildId, string userId)
    {
        if (rejected.Count == 0) return [];

        var sellsUpgrades = Env.License.IsHosted && Env.License.IsBillingConfigured;

        var needsGuildRemedy = sellsUpgrades && guildId is not null
            && rejected.Any(rejection => rejection.BoundBy == EntitlementBoundBy.Guild);

        var canManageGuild = needsGuildRemedy && await HasGuildPermissionAsync(
            guildId!, userId, ExternalPermission.ManageGuild);

        return rejected.Select(rejection => EntitlementDegradationDto.From(
            rejection.Degradation,
            rejection.Key,
            SubjectOf(rejection, guildId, userId),
            EntitlementRemedyPolicy.For(rejection.Cause, rejection.BoundBy, sellsUpgrades, canManageGuild),
            rejection.BoundBy)).ToList();
    }

    /// <summary>Whose limit it was.</summary>
    private static EntitlementSubject SubjectOf(RejectedUpload rejection, string? guildId, string userId) =>
        guildId is null || rejection.BoundBy == EntitlementBoundBy.User
            ? EntitlementSubject.ForUser(userId)
            : EntitlementSubject.ForGuild(guildId);

    private async Task<bool> HasGuildPermissionAsync(
        string guildId, string userId, ExternalPermission permission)
    {
        var response = await messageBus.InvokeAsync<HasUserPermissionToGuildResponse>(
            new HasUserPermissionToGuildRequest
            {
                GuildId = guildId,
                UserId = userId,
                Permission = permission,
            });

        return response.IsAllowed;
    }


    [HttpGet("{id}")]
    public async Task<IActionResult> GetAttachmentAsync(string id)
    {
        var attachment = context.Attachments.FirstOrDefault(a => a.Id == id);
        if (attachment is null) return NotFound();
        if (!await CanReadAsync(attachment)) return NotFound();

        var dto = attachment.ToFacet<Attachment, AttachmentDto>();
        dto.Url = $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/messaging/attachments/{id}/download";
        dto.ThumbnailUrl = $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/messaging/attachments/{id}/thumbnail";
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