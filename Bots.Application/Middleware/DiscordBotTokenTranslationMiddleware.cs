using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;

namespace Bots.Application.Middleware;

/// <summary>
/// Bridges Discord's static, long-lived bot tokens onto our short-lived OpenIddict JWTs.
/// Registered only in front of the /api/discord/v10 route group, before UseAuthentication(),
/// so it can rewrite the Authorization header before the normal JWT-bearer pipeline ever
/// sees the request - the compat surface and the native surface then share the exact same
/// auth/authorization code path.
/// </summary>
public class DiscordBotTokenTranslationMiddleware(RequestDelegate next, IHttpClientFactory httpClientFactory)
{
    private static readonly string IdentityBaseUrl =
        Environment.GetEnvironmentVariable("Services__Identity") ?? "http://_http.identity.default.svc.cluster.local";

    public async Task InvokeAsync(HttpContext context, IDistributedCache cache)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bot ", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (!DiscordCompatToken.TryUnpack(authHeader["Bot ".Length..], out var clientId, out var clientSecret))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var cacheKey = $"bot-jwt:{clientId}";
        var jwt = await cache.GetStringAsync(cacheKey);

        if (jwt is null)
        {
            var httpClient = httpClientFactory.CreateClient();
            var response = await httpClient.PostAsync($"{IdentityBaseUrl}/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }));

            if (!response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (payload?.AccessToken is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            jwt = payload.AccessToken;
            var ttl = TimeSpan.FromSeconds(Math.Max(payload.ExpiresIn - 60, 30));
            await cache.SetStringAsync(cacheKey, jwt, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
        }

        context.Request.Headers.Authorization = $"Bearer {jwt}";
        await next(context);
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
