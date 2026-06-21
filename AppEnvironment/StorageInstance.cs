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
      
            ForcePathStyle = true 
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