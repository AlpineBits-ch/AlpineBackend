using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Billing.Application.Promotions;

/// <summary>Everything promotions need, in one call.</summary>
public static class PromotionServiceCollectionExtensions
{
    public static IServiceCollection AddPromotions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Bound through the container's IConfiguration so this stays a one-argument call.
        services.AddOptions<PromotionOptions>().BindConfiguration(PromotionOptions.SectionName);

        services.AddSingleton<PromotionHasher>();
        services.AddScoped<PromotionCampaignService>();
        services.AddScoped<PromotionEligibilityService>();
        services.AddScoped<PromotionRedemptionService>();

        // The path that actually redeems.
        services.AddScoped<TrialService>();

        services.AddHostedService<PromotionExpirySweeper>();

        return services;
    }
}
