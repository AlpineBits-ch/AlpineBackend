using Billing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Billing.Infrastructure;

public static class BillingInfrastructure
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        return services;
    }

    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }
}
