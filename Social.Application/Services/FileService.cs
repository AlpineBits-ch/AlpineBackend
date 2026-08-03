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
    /// <summary>Image types accepted for avatars and banners.</summary>
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    public const long MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>Returns null when the upload is acceptable, otherwise the reason to reject it.</summary>
    public static string? ValidateImageUpload(IFormFile? file)
    {
        if (file is null || file.Length == 0) return "A file is required.";
        if (file.Length > MaxImageBytes) return $"File exceeds the {MaxImageBytes / (1024 * 1024)}MB limit.";
        if (!AllowedImageContentTypes.Contains(file.ContentType)) return "Unsupported image type.";
        return null;
    }

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

    // Banners use a "banner/" key prefix, distinct from the bare profileId key avatars use, so
    // the two images never collide in the shared bucket.
    private static string GetBannerKey(string profileId) => $"banner/{profileId}";

    public async Task<UploadedFile> UploadBannerAsync(IFormFile file, string profileId)
    {
        var config = Env.StorageConfiguration;
        string publicUrlBase = config.PublicUrl.TrimEnd('/');
        string key = GetBannerKey(profileId);

        try
        {
            await s3Client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = config.BucketName,
                Key = key
            });
        }
        catch (Exception)
        {
            // empty :D
        }

        using var stream = file.OpenReadStream();
        var putRequest = new PutObjectRequest
        {
            BucketName = config.BucketName,
            Key = key,
            ContentType = file.ContentType,
            InputStream = stream
        };

        await s3Client.PutObjectAsync(putRequest);

        string fileUrl = $"{publicUrlBase}/{config.BucketName}/{key}";

        return new UploadedFile
        {
            Id = profileId,
            Url = fileUrl,
            FileName = file.FileName,
            SizeBytes = file.Length,
            ContentType = file.ContentType
        };
    }

    public async Task<string?> GetPresignedUrlForBanner(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        var config = Env.StorageConfiguration;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = config.BucketName,
            Key = GetBannerKey(id),
            Expires = DateTime.UtcNow.AddMinutes(10),
            Verb = HttpVerb.GET
        };

        return s3Client.GetPreSignedURL(request);
    }
}