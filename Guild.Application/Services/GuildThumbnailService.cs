using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;

namespace Guild.Application.Services;

public class UploadedFile
{
    public string Id { get; init; }
    public string Url { get; set; }
    public string FileName { get; set; }
    public long SizeBytes { get; set; }
    public string ContentType { get; set; }
}
public class GuildThumbnailService(IAmazonS3 s3Client)
{
    public async Task<UploadedFile> UploadIconAsync(IFormFile file, string profileId)
    {
        var config = Env.StorageConfiguration;
        string publicUrlBase = config.PublicUrl.TrimEnd('/');

        // 1. Delete old icon if it exists
        try
        {
            await s3Client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = config.BucketName,
                Key = profileId
            });
        }
        catch (Exception)
        {
            // empty :D
        }

        // 2. Upload new icon
        using var stream = file.OpenReadStream();
        var putRequest = new PutObjectRequest
        {
            BucketName = config.BucketName,
            Key = profileId,
            ContentType = file.ContentType,
            InputStream = stream
        };

        await s3Client.PutObjectAsync(putRequest);

        // 3. Construct public download link
        string fileUrl = $"{publicUrlBase}/{config.BucketName}/{profileId}";

        return new UploadedFile
        {
            Id = profileId,
            Url = fileUrl,
            FileName = file.FileName,
            SizeBytes = file.Length,
            ContentType = file.ContentType
        };
    }

    public async Task<string?> GetPresignedUrlForIcon(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        var config = Env.StorageConfiguration;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = config.BucketName,
            Key = id,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Verb = HttpVerb.GET
        };

        // This works perfectly for GCS, AWS, and MinIO out of the box
        return s3Client.GetPreSignedURL(request);
    }
    
    public async Task<string?> GetPresignedUrlForThumbnail(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        
        var config = Env.StorageConfiguration;
        string thumbnailPath = $"thumbnails/{id}.jpg";

        var request = new GetPreSignedUrlRequest
        {
            BucketName = config.BucketName,
            Key = thumbnailPath,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Verb = HttpVerb.GET
        };

        return s3Client.GetPreSignedURL(request);
    }
}