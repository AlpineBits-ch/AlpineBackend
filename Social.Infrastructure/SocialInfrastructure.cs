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

            services.AddS3Storage();
    
        }
        catch (Exception e)
        {

        }
    }
}