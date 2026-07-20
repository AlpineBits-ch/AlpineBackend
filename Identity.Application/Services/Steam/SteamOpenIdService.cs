using System.Text.RegularExpressions;
using AppEnvironment;
using Microsoft.AspNetCore.WebUtilities;

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

    /// <summary>Callback path Steam is told to return the browser to.</summary>
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
    private static string ReturnToBase => Realm + CallbackPath;

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
            using var content = new FormUrlEncodedContent(form);
            using var response = await httpClient.PostAsync(SteamLoginEndpoint, content, ct);
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
