using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using AppEnvironment;

namespace Social.Api.Services;

public class UploadedFile
{
    public string Id { get; init; }
    public string Url { get; set; }
    public string FileName { get; set; }
    public long SizeBytes { get; set; }
    public string ContentType { get; set; }
}

public class FileService(IAmazonS3 s3Client)
{
    public async Task<UploadedFile> UploadAvatarAsync(IFormFile file, string profileId)
    {
        var config = Env.StorageConfiguration;
        string publicUrlBase = config.PublicUrl.TrimEnd('/');

        // 1. Attempt to delete the old avatar profile image if it exists
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

        // 2. Upload the new avatar file
        using var stream = file.OpenReadStream();
        var putRequest = new PutObjectRequest
        {
            BucketName = config.BucketName,
            Key = profileId,
            ContentType = file.ContentType,
            InputStream = stream
        };

        await s3Client.PutObjectAsync(putRequest);

        // 3. Formulate the cloud-agnostic public download link
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

    public async Task<string?> GetPresignedUrlForAvatar(string id)
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

        // Generates the presigned URL dynamically matching the user's host endpoint
        return s3Client.GetPreSignedURL(request);
    }
}