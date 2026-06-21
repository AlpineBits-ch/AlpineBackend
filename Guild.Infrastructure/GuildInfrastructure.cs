using Amazon.S3;
using AppEnvironment;

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
             
            services.AddS3Storage();

            
            var googleServiceAccountJsonBase64 = Env.GoogleServiceAccountJsonBase64;
            var googleServiceAccountJson = Convert.FromBase64String(googleServiceAccountJsonBase64);
            
            using var  storageCredentialMs = new MemoryStream(googleServiceAccountJson);
                 

        }
        catch (Exception _)
        {
            // Empty     
            Console.WriteLine("Could not create storage client, falling back to unauthenticated client");
        }
    }
}