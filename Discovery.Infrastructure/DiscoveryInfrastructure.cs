using Discovery.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Infrastructure;

public static class DiscoveryInfrastructure
{
    public static void AddInfrastructure(this IServiceCollection services) { }

    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        using var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }
}
