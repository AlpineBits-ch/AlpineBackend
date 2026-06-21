using Amazon.S3;
using AppEnvironment;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Messaging.Infrastructure;

public static class MessagingInfrastructure
{
    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        
        var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }

    public static void AddInfrastructure(this IServiceCollection services)
    {
        using var serviceProvider = services.BuildServiceProvider();
        var logger = LoggerFactory.Create(config =>
        {
            config.AddConsole();
        }).CreateLogger(typeof(MessagingInfrastructure));
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
            
            
            
            var firebaseServiceAccountBase64 = Env.FireBaseServiceAccountJsonBase64;
            var firebaseServiceAccountJson = Convert.FromBase64String(firebaseServiceAccountBase64);
            
            using var  fireBaseCredentialsMs = new MemoryStream(firebaseServiceAccountJson);
            var fireBaseCredentials = ServiceAccountCredential.FromServiceAccountData(fireBaseCredentialsMs);
            

            var app = FirebaseApp.Create(new AppOptions()
            {
                Credential = fireBaseCredentials.ToGoogleCredential()
            });
            services.AddSingleton(app);
            services.AddSingleton(storage);
        }
        catch (Exception _)
        {
            // Empty            
        }


        if (AppEnvironment.Env.MessagingConfiguration.UseScyllaDb)
        {
            logger.LogInformation("Using scylla db for message storage");
            services.AddScoped<IMessageRepository, ScyllaMessageRepository>();

        }
        else
        {
            logger.LogInformation("Using ef core for message storage");
            services.AddScoped<IMessageRepository, EfCoreMessageRepository>();
        }

    }
}