using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;

namespace Guild.Application.Services;

/// <summary>Mirrors GuildThumbnailService's S3 usage, keyed per-emoji instead of per-guild since a
/// guild can have many emoji (icons are 1-per-guild, so that service overwrites by profileId).</summary>
public class GuildEmojiService(IAmazonS3 s3Client)
{
    private static string GetKey(string guildId, string emojiId) => $"emojis/{guildId}/{emojiId}";

    public async Task UploadEmojiAsync(IFormFile file, string guildId, string emojiId)
    {
        var config = Env.StorageConfiguration;

        using var stream = file.OpenReadStream();
        await s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = config.BucketName,
            Key = GetKey(guildId, emojiId),
            ContentType = file.ContentType,
            InputStream = stream,
        });
    }

    public async Task DeleteEmojiAsync(string guildId, string emojiId)
    {
        var config = Env.StorageConfiguration;
        try
        {
            await s3Client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = config.BucketName,
                Key = GetKey(guildId, emojiId),
            });
        }
        catch (Exception)
        {
            // best-effort - matches GuildThumbnailService's icon-delete behavior
        }
    }

    public string GetPresignedUrl(string guildId, string emojiId)
    {
        var config = Env.StorageConfiguration;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = config.BucketName,
            Key = GetKey(guildId, emojiId),
            Expires = DateTime.UtcNow.AddHours(1),
            Verb = HttpVerb.GET,
        };

        return s3Client.GetPreSignedURL(request);
    }
}
