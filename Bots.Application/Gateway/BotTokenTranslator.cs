using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Bots.Application.Middleware;
using Microsoft.Extensions.Caching.Distributed;

namespace Bots.Application.Gateway;

public record BotAuthResult(bool Success, string? BotUserId, string? Jwt)
{
    public static readonly BotAuthResult Failed = new(false, null, null);
}

/// <summary>
/// Exchanges a Discord-compat bot token (base64(client_id:client_secret)) for a real OpenIddict JWT
/// via Identity's /connect/token, caching the JWT in Redis.
/// </summary>
public class BotTokenTranslator(IHttpClientFactory httpClientFactory, IDistributedCache cache)
{
    private static readonly string IdentityBaseUrl =
        Environment.GetEnvironmentVariable("Services__Identity") ?? "http://identity.default.svc.cluster.local:8080";

    private const int MaxCacheTtlSeconds = 600;

    public async Task<BotAuthResult> AuthenticateAsync(string discordToken)
    {
        if (!DiscordCompatToken.TryUnpack(discordToken, out var clientId, out var clientSecret))
            return BotAuthResult.Failed;

        // An empty secret can never be valid, and must not be allowed to probe the cache.
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return BotAuthResult.Failed;

        var jwt = await GetOrExchangeJwtAsync(clientId, clientSecret);
        return jwt is null ? BotAuthResult.Failed : new BotAuthResult(true, clientId, jwt);
    }

    /// <summary>The cache key binds the client id AND the presented secret.</summary>
    private async Task<string?> GetOrExchangeJwtAsync(string clientId, string clientSecret)
    {
        var cacheKey = BuildCacheKey(clientId, clientSecret);
        var jwt = await cache.GetStringAsync(cacheKey);
        if (jwt is not null) return jwt;

        var httpClient = httpClientFactory.CreateClient();
        var response = await httpClient.PostAsync($"{IdentityBaseUrl}/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }));

        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
        if (payload?.AccessToken is null) return null;

        jwt = payload.AccessToken;

        // Capped well below the token's own lifetime so that revocation - a secret reset or a
        // disabled bot account - takes effect in minutes rather than at the end of the full token
        // lifetime.
        var ttl = TimeSpan.FromSeconds(Math.Clamp(payload.ExpiresIn - 60, 30, MaxCacheTtlSeconds));
        await cache.SetStringAsync(cacheKey, jwt, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });

        return jwt;
    }

    internal static string BuildCacheKey(string clientId, string clientSecret)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret));
        return $"bot-jwt:{clientId}:{Convert.ToHexStringLower(digest)}";
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
