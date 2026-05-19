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
        try
        {
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

        services.AddScoped<IMessageRepository, ScyllaMessageRepository>();

    }
}