using AppEnvironment;
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
public class FileService(StorageClient client)
{
    public async Task<UploadedFile> UploadAvatarAsync(IFormFile file, string profileId)
    {
        try
        {
            await client.DeleteObjectAsync(Env.MessagingConfiguration.AwsBucketName, profileId);

        }
        catch (Exception _)
        {
            // empty :D
        }
        var uploadResult = await client.UploadObjectAsync(Env.MessagingConfiguration.AwsBucketName, profileId, file.ContentType, file.OpenReadStream(),
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

    public async Task<string?> GetPresignedUrlForAvatar(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        
        var signer = client.CreateUrlSigner();
        
        var data = await signer.SignAsync(Env.MessagingConfiguration.AwsBucketName, id, TimeSpan.FromMinutes(10));
        return data;
    }
}