using Amazon.S3;
using AppEnvironment;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
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
            services.AddS3Storage();

            var googleServiceAccountJsonBase64 = Env.GoogleServiceAccountJsonBase64;
            var googleServiceAccountJson = Convert.FromBase64String(googleServiceAccountJsonBase64);
            
            using var  storageCredentialMs = new MemoryStream(googleServiceAccountJson);

            
            
            
            var firebaseServiceAccountBase64 = Env.FireBaseServiceAccountJsonBase64;
            var firebaseServiceAccountJson = Convert.FromBase64String(firebaseServiceAccountBase64);
            
            using var  fireBaseCredentialsMs = new MemoryStream(firebaseServiceAccountJson);
            var fireBaseCredentials = ServiceAccountCredential.FromServiceAccountData(fireBaseCredentialsMs);
            

            var app = FirebaseApp.Create(new AppOptions()
            {
                Credential = fireBaseCredentials.ToGoogleCredential()
            });
            services.AddSingleton(app);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            
            // Empty            
        }


        if (AppEnvironment.Env.MessagingConfiguration.UseScyllaDb)
        {
            logger.LogInformation("Using scylla db for message storage");
            services.AddScoped<IMessageRepository, ScyllaMessageRepository>();
            services.AddScoped<IMentionIndexRepository, ScyllaMentionIndexRepository>();
        }
        else
        {
            logger.LogInformation("Using ef core for message storage");
            services.AddScoped<IMessageRepository, EfCoreMessageRepository>();
            services.AddScoped<IMentionIndexRepository, EfCoreMentionIndexRepository>();
        }

    }
}