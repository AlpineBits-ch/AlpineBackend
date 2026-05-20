using FFMpegCore;
using Google.Cloud.Storage.V1;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events;
using Messaging.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Distributed;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Wolverine.Persistence;

namespace Messaging.Application.Handler.Attachments;

public class ProcessAttachmentHandler
{
    public static async Task Handle(ProcessAttachment request, StorageClient storageClient, ILogger<ProcessAttachmentHandler> logger,  MicroserviceContext ctx, IDistributedCache cache)
    {
        string bucketName = "echo-chat";
        string thumbnailPath = $"thumbnails/{request.AttachmentId}.jpg";

        logger.LogInformation("Processing attachment {AttachmentId}", request.AttachmentId);
        
        var attachment = await ctx.Attachments.FindAsync(request.AttachmentId);
        
        try
        {
            if (request.ContentType.StartsWith("image/"))
            {
                using var sourceStream = new MemoryStream();
            
                await storageClient.DownloadObjectAsync(bucketName, request.AttachmentId, sourceStream);
                sourceStream.Position = 0;

                using var image = await Image.LoadAsync(sourceStream);
                image.Mutate(x => {
                 
                    x.BackgroundColor(Color.White); 

                    // 2. High-quality resize
                    x.Resize(new ResizeOptions {
                        Size = new Size(300, 300), 
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.Lanczos3
                    });
                });
                using var thumbStream = new MemoryStream();
                await image.SaveAsJpegAsync(thumbStream, new JpegEncoder { 
                    Quality = 90,
                    ColorType = JpegColorType.YCbCrRatio444 // Keeps colors crisp
                });
                thumbStream.Position = 0;   

                // 3. Upload Thumbnail
                await storageClient.UploadObjectAsync(
                    bucketName, 
                    thumbnailPath, 
                    "image/jpeg", 
                    thumbStream
                );
                
                sourceStream.Position = 0;
                await cache.SetAsync(MinimalAttachment.GetCacheId(attachment.Id), sourceStream.ToArray(),
                    new DistributedCacheEntryOptions()
                    {
                        SlidingExpiration = TimeSpan.FromMinutes(10)
                    });
            }
            else if (request.ContentType.StartsWith("video/"))
            {
                var tempVideoPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                var tempThumbPath = Path.ChangeExtension(tempVideoPath, ".jpg");

                try 
                {
                    // Download video to local disk for FFmpeg to seek
                    using (var fs = new FileStream(tempVideoPath, FileMode.Create))
                    {
                        await storageClient.DownloadObjectAsync(bucketName, request.AttachmentId, fs);
                    }

                    // Extract frame (FFMpegCore uses System.Drawing.Size)
                    await FFMpeg.SnapshotAsync(tempVideoPath, tempThumbPath, new System.Drawing.Size(320, 240), TimeSpan.FromSeconds(1));
            
                    using var thumbFileStream = File.OpenRead(tempThumbPath);
                    await storageClient.UploadObjectAsync(
                        bucketName, 
                        thumbnailPath, 
                        "image/jpeg", 
                        thumbFileStream
                    );
                    thumbFileStream.Position = 0;
                    
                    var ms =  new MemoryStream();
                    await thumbFileStream.CopyToAsync(ms);
                    await cache.SetAsync(MinimalAttachment.GetCacheId(attachment.Id), ms.ToArray(),
                        new DistributedCacheEntryOptions()
                        {
                            SlidingExpiration = TimeSpan.FromMinutes(10)
                        });
                    await ms.DisposeAsync();       
                }
                finally 
                {
                    if (File.Exists(tempVideoPath)) File.Delete(tempVideoPath);
                    if (File.Exists(tempThumbPath)) File.Delete(tempThumbPath);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        

        attachment?.ThumbnailUrl = "https://api.alpinebits.ch/api/v1/messaging/attachments/" + request.AttachmentId + "/thumbnail";
        attachment?.ThumbnailId = thumbnailPath;
        attachment?.State = AttachmentState.Complete;
    }
}