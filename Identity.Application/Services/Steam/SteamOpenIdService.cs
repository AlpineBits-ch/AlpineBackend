using System.Text.RegularExpressions;
using AppEnvironment;
using Microsoft.AspNetCore.WebUtilities;
using Polly;
using Polly.Extensions.Http;

namespace Identity.Application.Services.Steam;

/// <summary>
/// Implements the Steam OpenID 2.0 flow: builds the redirect to Steam and verifies the assertion
/// Steam sends back.
/// </summary>
public partial class SteamOpenIdService(HttpClient httpClient, ILogger<SteamOpenIdService> logger)
{
    private const string SteamLoginEndpoint = "https://steamcommunity.com/openid/login";
    private const string OpenIdNs = "http://specs.openid.net/auth/2.0";
    private const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";

    /// <summary>
    /// Steam's OpenID endpoint is prone to transient failures (5xx, timeouts, dropped connections),
    /// so every outbound call is routed through this policy.
    /// </summary>
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)),
            onRetry: (outcome, delay, attempt, _) => logger.LogWarning(
                outcome.Exception,
                "Transient failure calling Steam ({Status}); retry {Attempt}/3 in {Delay}ms",
                outcome.Result?.StatusCode,
                attempt,
                delay.TotalMilliseconds));

    /// <summary>
    /// Internal route the callback controller listens on (after the YARP gateway strips the
    /// /api/v1/identity prefix).
    /// </summary>
    public const string CallbackPath = "/api/v1/authentication/steam/callback";

    /// <summary>Custom OpenIddict grant type used to exchange a Steam login ticket for tokens.</summary>
    public const string SteamGrantType = "urn:echo:params:oauth:grant-type:steam";

    /// <summary>Token endpoint parameter carrying the one-time Steam login ticket.</summary>
    public const string TicketParameter = "steam_ticket";

    /// <summary>Redis key holding the user id a one-time Steam login ticket resolves to.</summary>
    public static string LoginTicketCacheKey(string ticket) => $"steam_login_ticket:{ticket}";

    // A SteamID64 is 17 digits. Steam issues https claimed ids, but accept http defensively.
    [GeneratedRegex(@"^https?://steamcommunity\.com/openid/id/(\d{17})$")]
    private static partial Regex ClaimedIdRegex();

    private static string Realm => Env.Steam.PublicBaseUrl.TrimEnd('/');
    private static string ReturnToBase => Realm + Env.Steam.PublicCallbackPath;

    /// <summary>
    /// Builds the URL the browser should be sent to in order to start authentication at Steam.
    /// </summary>
    public string BuildRedirectUrl(string stateId)
    {
        var returnTo = QueryHelpers.AddQueryString(ReturnToBase, "state", stateId);

        var parameters = new Dictionary<string, string?>
        {
            ["openid.ns"] = OpenIdNs,
            ["openid.mode"] = "checkid_setup",
            ["openid.return_to"] = returnTo,
            ["openid.realm"] = Realm,
            ["openid.identity"] = IdentifierSelect,
            ["openid.claimed_id"] = IdentifierSelect,
        };

        return QueryHelpers.AddQueryString(SteamLoginEndpoint, parameters);
    }

    /// <summary>Verifies the assertion Steam sent to the callback.</summary>
    public async Task<string?> VerifyAsync(IQueryCollection query, CancellationToken ct)
    {
        if (query["openid.mode"] != "id_res")
        {
            logger.LogWarning("Steam OpenID callback with unexpected mode {Mode}", query["openid.mode"].ToString());
            return null;
        }

        // Defensive: the signature covers return_to, but reject anything not aimed at our callback.
        var returnTo = query["openid.return_to"].ToString();
        if (!returnTo.StartsWith(ReturnToBase, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Steam OpenID return_to mismatch: {ReturnTo}", returnTo);
            return null;
        }

        // Echo every openid.* parameter back to Steam, flipping the mode to ask it to validate.
        var form = new Dictionary<string, string>();
        foreach (var pair in query)
        {
            if (pair.Key.StartsWith("openid.", StringComparison.Ordinal))
            {
                form[pair.Key] = pair.Value.ToString();
            }
        }
        form["openid.mode"] = "check_authentication";

        string body;
        try
        {
            // Rebuild the content on every attempt: HttpContent is single-use and would be disposed
            // after the first send, so the policy needs a fresh instance for each retry.
            using var response = await _retryPolicy.ExecuteAsync(
                async token => await httpClient.PostAsync(
                    SteamLoginEndpoint,
                    new FormUrlEncodedContent(form),
                    token),
                ct);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach Steam for OpenID verification");
            return null;
        }

        var isValid = body
            .Split('\n')
            .Any(line => line.Trim().Equals("is_valid:true", StringComparison.OrdinalIgnoreCase));

        if (!isValid)
        {
            logger.LogWarning("Steam OpenID assertion did not verify as valid");
            return null;
        }

        var claimedId = query["openid.claimed_id"].ToString();
        var match = ClaimedIdRegex().Match(claimedId);
        if (!match.Success)
        {
            logger.LogWarning("Steam claimed_id did not match the expected pattern: {ClaimedId}", claimedId);
            return null;
        }

        return match.Groups[1].Value;
    }

    /// <summary>
    /// The persona name and avatar behind a SteamID, when <c>STEAM_WEB_API_KEY</c> is configured.
    /// </summary>
    public async Task<SteamProfile?> GetProfileAsync(string steamId, CancellationToken ct)
    {
        var key = Env.Steam.WebApiKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            var url = QueryHelpers.AddQueryString(
                "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/",
                new Dictionary<string, string?> { ["key"] = key, ["steamids"] = steamId });

            using var response = await _retryPolicy.ExecuteAsync(
                async token => await httpClient.GetAsync(url, token), ct);

            if (!response.IsSuccessStatusCode) return null;

            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var player = document.RootElement
                .GetProperty("response").GetProperty("players")
                .EnumerateArray().FirstOrDefault();

            if (player.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            return new SteamProfile(
                steamId,
                player.TryGetProperty("personaname", out var name) ? name.GetString() : null,
                player.TryGetProperty("avatarmedium", out var avatar) ? avatar.GetString() : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the Steam profile for {SteamId}; continuing without it", steamId);
            return null;
        }
    }
}

/// <summary>Public Steam profile decoration. Every field but the id may be absent.</summary>
public sealed record SteamProfile(string SteamId, string? PersonaName, string? AvatarUrl);
