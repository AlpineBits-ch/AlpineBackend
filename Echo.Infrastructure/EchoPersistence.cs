using Echo.Persistence.Persistance;
using Microsoft.EntityFrameworkCore;

namespace Echo.Persistence;

public static class EchoPersistence
{
    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }

    public static void AddInfrastructure(this IServiceCollection services)
    {
        
    }
}