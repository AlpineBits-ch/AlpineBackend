using AppEnvironment;
using Isle.Api;
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
             
            

        }
        catch (Exception _)
        {
            // Empty     
            Console.WriteLine("Could not create storage client, falling back to unauthenticated client");
        }
    }
}