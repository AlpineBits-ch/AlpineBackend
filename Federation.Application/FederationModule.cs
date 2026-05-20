using Microsoft.Extensions.DependencyInjection;

namespace Federation.Application;

public static class FederationModule
{
    public static IServiceCollection AddFederation(this IServiceCollection services)
    {
        return services;
    }
}