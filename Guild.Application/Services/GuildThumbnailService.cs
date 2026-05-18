using Google.Cloud.Storage.V1;
using Persistence;

namespace Social.Api.Services;

public class UploadedFile
{
    public string Id { get; init; }
    public string Url { get; set; }
    public string FileName { get; set; }
    public long SizeBytes { get; set; }
    public string ContentType { get; set; }
}
public class GuildThumbnailService(StorageClient client)
{
    public async Task<UploadedFile> UploadIconAsync(IFormFile file, string profileId)
    {
        try
        {
            await client.DeleteObjectAsync("echo-chat", profileId);

        }
        catch (Exception _)
        {
            // empty :D
        }
        var uploadResult = await client.UploadObjectAsync("echo-chat", profileId, file.ContentType, file.OpenReadStream(),
            new UploadObjectOptions()
            {

            });

        return new UploadedFile()
        {
            Id = profileId,
            Url = uploadResult.MediaLink,
            FileName = file.FileName,
            SizeBytes = file.Length,
            ContentType = file.ContentType
        };
    }

    public async Task<string?> GetPresignedUrlForIcon(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        
        var signer = client.CreateUrlSigner();
        
        var data = await signer.SignAsync("echo-chat", id, TimeSpan.FromMinutes(10));
        return data;
    }
    
    public async Task<string?> GetPresignedUrlForThumbnail(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        
        var signer = client.CreateUrlSigner();
        
        string thumbnailPath = $"thumbnails/{id}.jpg";

        var data = await signer.SignAsync("echo-chat", thumbnailPath, TimeSpan.FromMinutes(10));
        return data;
    }
}