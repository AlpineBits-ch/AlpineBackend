using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;

namespace Identity.Application.Services.DataExport;

/// <summary>
/// Where a finished export archive lives, and how a caller is let at it.
///
/// <para>An interface rather than the bare <c>IAmazonS3</c> the avatar/banner paths use, because
/// everything interesting about this feature - "a not-ready export is a 409", "an expired one is a
/// 410", "the sweep deletes the object" - is decided around the store, not inside it, and none of
/// that is testable if the only seam is a live bucket.</para>
/// </summary>
public interface IDataExportArtifactStore
{
    Task PutAsync(string key, byte[] content, CancellationToken ct = default);

    /// <summary>A short-lived URL that carries its own authorization. The caller has already been
    /// authenticated and checked for ownership; this is the redirect target, not the access
    /// control.</summary>
    Task<string> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// The real store, on the same S3-compatible bucket every other artifact in this system uses (see
/// <c>AppEnvironment.StorageInstance</c>).
///
/// <para><b>Delete swallows its exceptions; put does not.</b> A failed upload must fail the export -
/// otherwise the row goes <c>Ready</c> pointing at an object that is not there, and the subject gets
/// a 302 to nothing. A failed delete during the expiry sweep must not stop the row being marked
/// <c>Expired</c>: the object is already unreachable through the API at that point, and a sweep that
/// aborts on one stubborn key stops expiring every key behind it.</para>
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
            // Not inherited from ServiceURL - GetPreSignedUrlRequest.Protocol is its own setting and
            // defaults to HTTPS, so a deployment whose bucket is plain HTTP (compose.yaml and the
            // self-hosting installers both use SERVICE_URL=http://minio:9000) redirected the subject
            // to https:// on a port that speaks http. See Env.StorageConfiguration's remarks: this
            // route is the first place a signed URL is handed to a person rather than followed by
            // another service, which is why it is the one that noticed.
            Protocol = Env.StorageConfiguration.ServiceUrlIsPlainHttp ? Protocol.HTTP : Protocol.HTTPS,
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
