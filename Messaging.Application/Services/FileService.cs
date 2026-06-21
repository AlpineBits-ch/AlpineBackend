using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using AppEnvironment;
using Messaging.Domain.Entities;
using Microsoft.Extensions.Configuration; // Ensure this is included

namespace Messaging.Application.Services;

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
    public async Task<ICollection<UploadedFile>> UploadFileAsync(ICollection<IFormFile> files)
    {
        var responses = new List<UploadedFile>();

        string bucketName = Env.StorageConfiguration.BucketName;
        string publicUrlBase = Env.StorageConfiguration.PublicUrl;

        foreach (var file in files)
        {
            var id = Attachment.GenerateId();

            using var stream = file.OpenReadStream();
            
            var putRequest = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = id,
                ContentType = file.ContentType,
                InputStream = stream
            };

            await s3Client.PutObjectAsync(putRequest);

         
            string fileUrl = $"{publicUrlBase}/{bucketName}/{id}";

            responses.Add(new UploadedFile
            {
                Id = id,
                Url = fileUrl,
                FileName = file.FileName,
                SizeBytes = file.Length,
                ContentType = file.ContentType
            });
        }

        return responses;
    }
}