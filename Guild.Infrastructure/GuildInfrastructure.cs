using Amazon.S3;
using AppEnvironment;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedLockNet.SERedis;
using StackExchange.Redis;

namespace Guild.Persistence;
using Microsoft.AspNetCore.Builder;
public static class GuildInfrastructure
{
    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }

    public static void AddInfrastructure(this IServiceCollection services)
    {
        try
        {
            var redis = Env.Redis;
            IConnectionMultiplexer multiplexer = ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}");
        
            services.AddSingleton<IConnectionMultiplexer>(multiplexer);
          
        }
            
        catch (Exception e)
        {
            Console.WriteLine(e);
            // emptY;
        }
        try
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
            
            var googleServiceAccountJsonBase64 = Env.GoogleServiceAccountJsonBase64;
            var googleServiceAccountJson = Convert.FromBase64String(googleServiceAccountJsonBase64);
            
            using var  storageCredentialMs = new MemoryStream(googleServiceAccountJson);
            var storageClientCredential = ServiceAccountCredential.FromServiceAccountData(storageCredentialMs);

            var storage = StorageClient.Create(storageClientCredential.ToGoogleCredential());
            services.AddSingleton(storage);           

        }
        catch (Exception _)
        {
            services.AddSingleton(StorageClient.CreateUnauthenticated());
            // Empty     
            Console.WriteLine("Could not create storage client, falling back to unauthenticated client");
        }
    }
}