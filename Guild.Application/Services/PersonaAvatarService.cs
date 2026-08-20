using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;

namespace Guild.Application.Services;

/// <summary>
/// Object storage for character avatars: one object per persona, and one more per guild whose
/// profile overrides it.
/// </summary>
public class PersonaAvatarService(IAmazonS3 s3Client)
{
    private const int PresignedUrlMinutes = 10;

    /// <summary>The object behind <c>Persona.AvatarUrl</c>.</summary>
    public static string GlobalKey(string personaId) => $"persona-avatars/{personaId}/global";

    /// <summary>The object behind <c>PersonaGuildProfile.AvatarUrl</c>.</summary>
    public static string GuildKey(string personaId, string guildId) => $"persona-avatars/{personaId}/{guildId}";

    /// <summary>
    /// The value written to <c>Persona.AvatarUrl</c>. The gateway serves this service under
    /// /api/v1/guild, so a URL composed from the route as this service sees it would 404 for
    /// every client.
    /// </summary>
    /// <param name="personaId">The character the avatar belongs to.</param>
    /// <param name="version">A value from <see cref="Version"/>.</param>
    /// <returns>An absolute URL on this instance.</returns>
    public static string PublicUrlFor(string personaId, long version) =>
        $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/guild/personas/{personaId}/avatar?v={version}";

    /// <summary>The value written to <c>PersonaGuildProfile.AvatarUrl</c>.</summary>
    /// <param name="personaId">The character the avatar belongs to.</param>
    /// <param name="guildId">The guild overriding it.</param>
    /// <param name="version">A value from <see cref="Version"/>.</param>
    /// <returns>An absolute URL on this instance.</returns>
    public static string PublicUrlFor(string personaId, string guildId, long version) =>
        $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/guild/guilds/{guildId}/personas/{personaId}/profile/avatar?v={version}";

    /// <summary>A cache-busting stamp for the URL above.</summary>
    /// <returns>Unix milliseconds, so two uploads in the same second still differ.</returns>
    public static long Version() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Writes the bytes, overwriting whatever the key held.</summary>
    /// <param name="file">The uploaded file.</param>
    /// <param name="key">A key from <see cref="GlobalKey"/> or <see cref="GuildKey"/>.</param>
    /// <param name="contentType">The type to serve the object as, taken from the route's allowlist rather than from the caller.</param>
    public async Task UploadAsync(IFormFile file, string key, string contentType)
    {
        await using var stream = file.OpenReadStream();
        await s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = key,
            ContentType = contentType,
            InputStream = stream,
        });
    }

    /// <summary>Removes the object, best-effort.</summary>
    /// <param name="key">A key from <see cref="GlobalKey"/> or <see cref="GuildKey"/>.</param>
    public async Task DeleteAsync(string key)
    {
        try
        {
            await s3Client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Env.StorageConfiguration.BucketName,
                Key = key,
            });
        }
        catch (Exception)
        {
            // An orphaned object costs storage; a failed removal the caller retries against an
            // avatar that is already unreferenced costs them the feature.
        }
    }

    /// <summary>Signs a read of the object.</summary>
    /// <param name="key">A key from <see cref="GlobalKey"/> or <see cref="GuildKey"/>.</param>
    /// <returns>A presigned GET URL.</returns>
    public string GetPresignedUrl(string key) =>
        s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Env.StorageConfiguration.BucketName,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(PresignedUrlMinutes),
            Verb = HttpVerb.GET,
        });
}
