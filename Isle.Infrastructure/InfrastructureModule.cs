using AppEnvironment;
using Echo.Realtime.Caching;
using Isle.Domain;
using Isle.Infrastructure.Persistence;
using Isle.Infrastructure.Sfu;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Isle.Infrastructure;

public static class IsleInfrastructure
{
    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }

    public static void AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<ISfuClient, RealtimeSfuClient>();
        services.AddSingleton<IsleVoiceRoom>();

        try
        {
            var redis = Env.Redis;
            IConnectionMultiplexer multiplexer = ConnectionMultiplexer.Connect($"{redis.Host}:{redis.Port},password={redis.Password}");

            services.AddSingleton<IConnectionMultiplexer>(multiplexer);

            // Registered inside the same try, and only once the multiplexer exists: it takes one, so
            // registering it against a connection that could not be made would turn "Redis is down"
            // into a resolution failure for everything that takes the lock optionally.
            services.AddSingleton<IDistributedLockService, RedisDistributedLockService>();
        }
            
        catch (Exception e)
        {
            Console.WriteLine(e);
            // emptY;
        }
        try
        {
             
            

        }
        catch (Exception _)
        {
            // Empty     
            Console.WriteLine("Could not create storage client, falling back to unauthenticated client");
        }
    }
}