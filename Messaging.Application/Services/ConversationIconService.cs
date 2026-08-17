using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;
using Microsoft.AspNetCore.Http;

namespace Messaging.Application.Services;

/// <summary>One stored group icon, open for streaming back to the caller.</summary>
public sealed record StoredIcon(Stream Content, string ContentType);

/// <summary>Group conversation icons, one object per conversation.</summary>
public class ConversationIconService(IAmazonS3 s3Client)
{
    /// <summary>Prefixed so an icon can never collide with an attachment, which is keyed by bare id.</summary>
    public static string KeyFor(string conversationId) => $"conversation-icons/{conversationId}";

    public async Task UploadAsync(string conversationId, IFormFile file, CancellationToken ct = default)
    {
        await using var stream = file.OpenReadStream();
        await s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = KeyFor(conversationId),
            ContentType = file.ContentType,
            InputStream = stream,
        }, ct);
    }

    /// <summary>The stored bytes, or null when nothing is there.</summary>
    public async Task<StoredIcon?> GetAsync(string conversationId, CancellationToken ct = default)
    {
        try
        {
            var response = await s3Client.GetObjectAsync(
                Env.StorageConfiguration.BucketName, KeyFor(conversationId), ct);

            return new StoredIcon(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string conversationId, CancellationToken ct = default) =>
        s3Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = KeyFor(conversationId),
        }, ct);
}
