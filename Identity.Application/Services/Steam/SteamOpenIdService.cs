using System.Text.RegularExpressions;
using AppEnvironment;
using Microsoft.AspNetCore.WebUtilities;
using Polly;
using Polly.Extensions.Http;

namespace Identity.Application.Services.Steam;

/// <summary>
/// Implements the Steam OpenID 2.0 flow: builds the redirect to Steam and verifies the assertion
/// Steam sends back. Steam is an OpenID 2.0 provider (not OAuth2/OIDC), so verification is done in
/// stateless ("dumb") mode by echoing the received parameters back with
/// <c>openid.mode=check_authentication</c> and confirming the response contains <c>is_valid:true</c>.
/// </summary>
public partial class SteamOpenIdService(HttpClient httpClient, ILogger<SteamOpenIdService> logger)
{
    private const string SteamLoginEndpoint = "https://steamcommunity.com/openid/login";
    private const string OpenIdNs = "http://specs.openid.net/auth/2.0";
    private const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";

    /// <summary>
    /// Steam's OpenID endpoint is prone to transient failures (5xx, timeouts, dropped connections),
    /// so every outbound call is routed through this policy. It retries transient HTTP errors with
    /// exponential backoff; non-transient responses and caller cancellation pass straight through.
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
    /// /api/v1/identity prefix). The browser-facing return_to is built from
    /// <see cref="AppEnvironment.SteamConfiguration.PublicCallbackPath"/> instead.
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
    /// The <paramref name="stateId"/> is round-tripped via the return_to query so the callback can
    /// recover the flow context (link vs login, target user).
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

    /// <summary>
    /// Verifies the assertion Steam sent to the callback. Returns the authenticated SteamID64 on
    /// success, or <c>null</c> if the assertion is missing, malformed, or fails verification.
    /// </summary>
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
}
