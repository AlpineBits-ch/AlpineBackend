using AppEnvironment;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
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
            var googleServiceAccountJsonBase64 = Env.GoogleServiceAccountJsonBase64;
            var credsJson = Convert.FromBase64String(googleServiceAccountJsonBase64);
            
                
            using var  ms = new MemoryStream(credsJson);
            var credential = ServiceAccountCredential.FromServiceAccountData(ms);

            var storage = StorageClient.Create(credential.ToGoogleCredential());
            services.AddSingleton(storage);
            
            
        }
        catch (Exception e)
        {
            services.AddSingleton(StorageClient.CreateUnauthenticated());

        }
    }
}