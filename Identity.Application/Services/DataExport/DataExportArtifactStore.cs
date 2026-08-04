using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;

namespace Identity.Application.Services.DataExport;

/// <summary>Where a finished export archive lives, and how a caller is let at it.</summary>
public interface IDataExportArtifactStore
{
    Task PutAsync(string key, byte[] content, CancellationToken ct = default);

    /// <summary>A short-lived URL that carries its own authorization.</summary>
    Task<string> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// The real store, on the same S3-compatible bucket every other artifact in this system uses (see
/// <c>AppEnvironment.StorageInstance</c>).
/// </summary>
public class S3DataExportArtifactStore(IAmazonS3 s3, ILogger<S3DataExportArtifactStore> logger)
    : IDataExportArtifactStore
{
    public async Task PutAsync(string key, byte[] content, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(content, writable: false);

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = key,
            ContentType = "application/zip",
            InputStream = stream,
        }, ct);
    }

    public Task<string> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default)
    {
        var url = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = key,
            Expires = DateTime.UtcNow.Add(lifetime),
            Verb = HttpVerb.GET,
        });

        return Task.FromResult(url);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Env.StorageConfiguration.BucketName,
                Key = key,
            }, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to delete expired data-export artifact {Key}", key);
        }
    }
}
