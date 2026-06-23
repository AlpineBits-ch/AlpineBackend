using Federation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Federation.Infrastructure;

public static class InfrastructureModule
{
    public static IServiceCollection AddFederationInfrastructure(this IServiceCollection services)
    {
        return services;
    }

    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }
}
