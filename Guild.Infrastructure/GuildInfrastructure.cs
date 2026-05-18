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
            var storage = StorageClient.Create();

           
          
            services.AddSingleton(storage);
        }
        catch (Exception _)
        {
            services.AddSingleton(StorageClient.CreateUnauthenticated());
            // Empty            
        }
    }
}