using System.Net.Http.Json;
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
        Environment.GetEnvironmentVariable("Services__Identity") ?? "http://_http.identity.default.svc.cluster.local";

    public async Task<BotAuthResult> AuthenticateAsync(string discordToken)
    {
        if (!DiscordCompatToken.TryUnpack(discordToken, out var clientId, out var clientSecret))
            return BotAuthResult.Failed;

        var jwt = await GetOrExchangeJwtAsync(clientId, clientSecret);
        return jwt is null ? BotAuthResult.Failed : new BotAuthResult(true, clientId, jwt);
    }

    private async Task<string?> GetOrExchangeJwtAsync(string clientId, string clientSecret)
    {
        var cacheKey = $"bot-jwt:{clientId}";
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
        var ttl = TimeSpan.FromSeconds(Math.Max(payload.ExpiresIn - 60, 30));
        await cache.SetStringAsync(cacheKey, jwt, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });

        return jwt;
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
