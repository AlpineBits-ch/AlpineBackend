using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;

namespace Guild.Application.Services;

/// <summary>
/// Object storage for role badge images, keyed per role the way <see cref="GuildEmojiService"/> is
/// keyed per emoji.
/// </summary>
public class RoleIconService(IAmazonS3 s3Client)
{
    private static string GetKey(string guildId, string roleId) => $"role-icons/{guildId}/{roleId}";

    /// <summary>
    /// The value written to <c>Role.IconUrl</c>: an absolute URL on this instance pointing at the
    /// redirect route below.
    /// </summary>
    public static string PublicUrlFor(string guildId, string roleId) =>
        $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/guilds/{guildId}/roles/{roleId}/icon";

    public async Task UploadAsync(IFormFile file, string guildId, string roleId)
    {
        var config = Env.StorageConfiguration;

        await using var stream = file.OpenReadStream();
        await s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = config.BucketName,
            Key = GetKey(guildId, roleId),
            ContentType = file.ContentType,
            InputStream = stream,
        });
    }

    /// <summary>Best-effort, matching <see cref="GuildEmojiService.DeleteEmojiAsync"/>: the role's
    /// badge has already been cleared in the database by the time this runs, so an orphaned object
    /// costs storage and nothing else, whereas failing the request would leave a role whose icon the
    /// client still shows.</summary>
    public async Task DeleteAsync(string guildId, string roleId)
    {
        var config = Env.StorageConfiguration;
        try
        {
            await s3Client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = config.BucketName,
                Key = GetKey(guildId, roleId),
            });
        }
        catch (Exception)
        {
            // Nothing to recover here - see the summary.
        }
    }

    public string GetPresignedUrl(string guildId, string roleId)
    {
        var config = Env.StorageConfiguration;

        return s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = config.BucketName,
            Key = GetKey(guildId, roleId),
            Expires = DateTime.UtcNow.AddMinutes(10),
            Verb = HttpVerb.GET,
        });
    }
}
