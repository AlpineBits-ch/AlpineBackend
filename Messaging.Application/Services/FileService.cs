using AppEnvironment;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Messaging.Domain.Entities;

namespace Messaging.Application.Services;

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
    public async Task<ICollection<UploadedFile>> UploadFileAsync(ICollection<IFormFile> files)
    {

        var responses = new List<UploadedFile>();

        foreach (var file in files)
        {
            var id = Attachment.GenerateId();

            var uploadResult = await client.UploadObjectAsync(Env.MessagingConfiguration.AwsBucketName, id, file.ContentType, file.OpenReadStream(),
                new UploadObjectOptions()
                {

                });

            responses.Add(new UploadedFile()
            {
                Id = id,
                Url = uploadResult.MediaLink,
                FileName = file.FileName,
                SizeBytes = file.Length,
                ContentType = file.ContentType

            });

        }

        return responses.ToList();
    }

}