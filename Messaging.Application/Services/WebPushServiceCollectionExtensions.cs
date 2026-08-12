namespace Messaging.Application.Services;

/// <summary>Registers <see cref="WebPushSender"/> and the named HTTP client it resolves.</summary>
public static class WebPushServiceCollectionExtensions
{
    /// <summary>How long to wait on a push service before giving up on one subscription.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Adds Web Push sending.</summary>
    public static IServiceCollection AddWebPush(this IServiceCollection services)
    {
        // A named client, so the sender's own registration below stays an implementation-type one.
        services.AddHttpClient(WebPushSender.HttpClientName, client => client.Timeout = RequestTimeout);

        services.AddScoped<WebPushSender>();

        return services;
    }
}
