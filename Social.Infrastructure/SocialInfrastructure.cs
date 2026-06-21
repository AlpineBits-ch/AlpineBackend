using Amazon.S3;
using AppEnvironment;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Social.Infrastructure.Persistence;

namespace Social.Infrastructure;

public static class SocialInfrastructure
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
            var credsJson = Convert.FromBase64String(googleServiceAccountJsonBase64);
            
                
            using var  ms = new MemoryStream(credsJson);
       
            
            
        }
        catch (Exception e)
        {

        }
    }
}