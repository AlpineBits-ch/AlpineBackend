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
/// Exchanges a Discord-compat bot token (base64(client_id:client_secret)) for a real OpenIddict
/// JWT via Identity's /connect/token, caching the JWT in Redis. Shared by
/// <see cref="DiscordBotTokenTranslationMiddleware"/> (the raw token comes from an
/// `Authorization: Bot ...` header) and the Gateway WebSocket's IDENTIFY handler (the raw token
/// comes from the Identify payload's `token` field instead) - both authenticate a bot the exact
/// same way, just from a different place the token string arrives from. A successful exchange
/// (or cache hit) against OpenIddict *is* the auth check - no separate JWT validation needed.
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

    /// <summary>
    /// The cache key binds the client id AND the presented secret. Keying on the client id alone
    /// was an authentication bypass: a bot's client id is its BotUserId, which is public (it is the
    /// author id on every message the bot posts), so once any cache entry existed for a bot,
    /// anyone could present that id with an arbitrary secret and receive the bot's real JWT
    /// without the secret ever being checked. With the secret in the key a wrong secret cannot
    /// hit a cached entry and always falls through to /connect/token, which rejects it.
    ///
    /// The secret is hashed rather than embedded so the plaintext never lands in Redis keyspace.
    /// </summary>
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
        // lifetime. The cost is one /connect/token call per bot per cap interval, which is
        // negligible next to the per-request traffic this cache serves.
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
