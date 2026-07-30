using System.Net.Http.Headers;
using System.Net.Http.Json;
using AppEnvironment;
using Polly;
using Polly.Extensions.Http;

namespace Import.Application.Discord;

public static class DiscordApiClientRegistration
{
    public const string HttpClientName = "DiscordBotApi";

    public static void AddDiscordApiClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://discord.com/api/v10/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", Env.DiscordImport.BotToken);
        });
        services.AddScoped<DiscordApiClient>();
    }
}

/// <summary>
/// Thin wrapper around the real discord.com REST API using the Echo-owned import bot's token.
/// Call volume per import/reconciliation pass is tiny (a handful of requests), so no bucket-
/// tracking rate limiter is used - just resilience against transient failures and 429s via Polly,
/// same shape as Identity.Application/Services/Steam/SteamOpenIdService.cs.
/// </summary>
public class DiscordApiClient(IHttpClientFactory httpClientFactory, ILogger<DiscordApiClient> logger)
{
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(r => (int)r.StatusCode == 429)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: (attempt, outcome, _) =>
                outcome.Result?.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)),
            onRetryAsync: (outcome, delay, attempt, _) =>
            {
                logger.LogWarning(
                    outcome.Exception,
                    "Transient failure calling Discord API ({Status}); retry {Attempt}/3 in {Delay}ms",
                    outcome.Result?.StatusCode,
                    attempt,
                    delay.TotalMilliseconds);
                return Task.CompletedTask;
            });

    private HttpClient Client => httpClientFactory.CreateClient(DiscordApiClientRegistration.HttpClientName);

    public async Task<DiscordGuildPayload?> GetGuildAsync(string discordGuildId, CancellationToken ct = default)
    {
        using var response = await _retryPolicy.ExecuteAsync(t => Client.GetAsync($"guilds/{discordGuildId}", t), ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<DiscordGuildPayload>(cancellationToken: ct);
    }

    public async Task<List<DiscordRolePayload>> GetGuildRolesAsync(string discordGuildId, CancellationToken ct = default)
    {
        using var response = await _retryPolicy.ExecuteAsync(t => Client.GetAsync($"guilds/{discordGuildId}/roles", t), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DiscordRolePayload>>(cancellationToken: ct) ?? [];
    }

    public async Task<List<DiscordChannelPayload>> GetGuildChannelsAsync(string discordGuildId, CancellationToken ct = default)
    {
        using var response = await _retryPolicy.ExecuteAsync(t => Client.GetAsync($"guilds/{discordGuildId}/channels", t), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DiscordChannelPayload>>(cancellationToken: ct) ?? [];
    }

    /// <summary>Null when the guild has no onboarding configured, or when the bot lacks
    /// MANAGE_GUILD on it - neither is an import failure, the rest of the structure still imports.</summary>
    public async Task<DiscordOnboardingPayload?> GetGuildOnboardingAsync(string discordGuildId, CancellationToken ct = default)
    {
        using var response = await _retryPolicy.ExecuteAsync(t => Client.GetAsync($"guilds/{discordGuildId}/onboarding", t), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("No onboarding config imported for Discord guild {DiscordGuildId} ({Status})",
                discordGuildId, response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DiscordOnboardingPayload>(cancellationToken: ct);
    }

    /// <summary>Null for guilds without a welcome screen (the common case - it is a Community
    /// feature), which Discord serves as a 404.</summary>
    public async Task<DiscordWelcomeScreenPayload?> GetGuildWelcomeScreenAsync(string discordGuildId, CancellationToken ct = default)
    {
        using var response = await _retryPolicy.ExecuteAsync(t => Client.GetAsync($"guilds/{discordGuildId}/welcome-screen", t), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("No welcome screen imported for Discord guild {DiscordGuildId} ({Status})",
                discordGuildId, response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DiscordWelcomeScreenPayload>(cancellationToken: ct);
    }

    /// <summary>Used by the unlink flow to have the bot leave the Discord server when a
    /// GuildLink is revoked.</summary>
    public async Task LeaveGuildAsync(string discordGuildId, CancellationToken ct = default)
    {
        using var response = await _retryPolicy.ExecuteAsync(t => Client.DeleteAsync($"users/@me/guilds/{discordGuildId}", t), ct);
        response.EnsureSuccessStatusCode();
    }
}
