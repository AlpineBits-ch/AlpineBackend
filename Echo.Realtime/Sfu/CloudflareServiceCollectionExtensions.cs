using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace Echo.Realtime.Sfu;

public static class CloudflareServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CloudflareService"/> and the named <c>CloudflareProxy</c> HTTP client it
    /// resolves, pointed at the given Cloudflare Calls app and authenticated with its API token.
    /// </summary>
    public static IServiceCollection AddCloudflareCalls(this IServiceCollection services, string appId, string apiToken)
    {
        services.AddHttpClient("CloudflareProxy", client =>
        {
            client.BaseAddress = new Uri($"https://rtc.live.cloudflare.com/v1/apps/{appId}/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        });

        services.AddScoped<CloudflareService>();

        return services;
    }
}
