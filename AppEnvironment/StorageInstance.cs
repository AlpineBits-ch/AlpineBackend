using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;

namespace AppEnvironment;

public static class StorageInstance
{
    public static IServiceCollection AddS3Storage(this IServiceCollection services)
    {
        var storageConfig = Env.StorageConfiguration;

        var s3Config = new AmazonS3Config
        {
            ForcePathStyle = true,
            // GCS's S3-interop XML API rejects the aws-chunked flexible-checksum
            // trailer that the SDK sends by default since v4 (WHEN_SUPPORTED) with a bare 400.
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED
        };

        if (storageConfig.UseServiceUrl)
        {
            s3Config.ServiceURL = storageConfig.ServiceUrl;
        }
        else
        {
               
            string region = Env.StorageConfiguration.Region;
            s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        var credentials = new Amazon.Runtime.BasicAWSCredentials(
            storageConfig.AccessKey,
            storageConfig.SecretKey
        );

        services.AddSingleton<IAmazonS3>(new AmazonS3Client(credentials, s3Config));
        return services;
    }
}