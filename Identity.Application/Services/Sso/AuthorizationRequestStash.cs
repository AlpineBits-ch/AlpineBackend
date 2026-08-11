using System.Text.Json;
using Ids;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using OpenIddict.Abstractions;

namespace Identity.Application.Services.Sso;

/// <summary>
/// A validated authorization (or end-session) request, parked in Redis while the person signs in.
/// </summary>
public sealed class StashedRequest
{
    public required string Kind { get; init; }

    /// <summary>
    /// Every parameter OpenIddict accepted, so the request can be reconstituted verbatim when the
    /// browser comes back.
    /// </summary>
    public required Dictionary<string, string> Parameters { get; init; }

    public string? ClientId { get; init; }
    public string? RedirectUri { get; init; }
    public string? LoginHint { get; init; }
    public string[] Scopes { get; init; } = [];

    /// <summary>When the request was first parked.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Set once the person has been sent to the login page for this request.</summary>
    public bool Reprompted { get; set; }

    /// <summary>Subject that granted consent, or confirmed a sign-out.</summary>
    public string? DecidedBy { get; set; }

    /// <summary>The answer, when <see cref="Confirmed"/> is set.</summary>
    public bool ConsentGranted { get; set; }

    /// <summary>
    /// The person has answered: approved or refused the consent screen, or confirmed a sign-out.
    /// </summary>
    public bool Confirmed { get; set; }
}

/// <summary>
/// Redis-backed, opaque, single-use storage for in-flight authorization requests.
/// </summary>
public sealed class AuthorizationRequestStash(IDistributedCache cache)
{
    public const string AuthorizeKind = "authorize";
    public const string LogoutKind = "logout";

    /// <summary>The parameter the resumed request carries the stash id in.</summary>
    public const string Parameter = "rq";

    /// <summary>
    /// Long enough for a password reset detour or a QR code that had to be regenerated once; short
    /// enough that an abandoned request is not still redeemable an hour later.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Key(string id) => $"sso_authrq:{id}";

    public async Task<string> StashAsync(string kind, OpenIddictRequest request, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in request.GetParameters())
        {
            // A client secret has no business on an authorization request and must never be
            // written to a cache entry; `rq` is ours and re-added by the resume redirect.
            if (name is OpenIddictConstants.Parameters.ClientSecret or Parameter) continue;

            var rendered = (string?)value;
            if (rendered is not null) parameters[name] = rendered;
        }

        var stashed = new StashedRequest
        {
            Kind = kind,
            Parameters = parameters,
            ClientId = request.ClientId,
            RedirectUri = request.RedirectUri,
            LoginHint = request.LoginHint,
            Scopes = [.. request.GetScopes()],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var id = Identifier.New("authrq");
        await SaveAsync(id, stashed, ct);
        return id;
    }

    public async Task<StashedRequest?> PeekAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var json = await cache.GetStringAsync(Key(id), ct);
        if (json is null) return null;

        var stashed = JsonSerializer.Deserialize<StashedRequest>(json, Json);

        // Belt and braces over the cache's own TTL: SaveAsync re-parks an updated entry, and the
        // absolute age is measured from the original CreatedAt so consent cannot extend it.
        if (stashed is null || DateTimeOffset.UtcNow - stashed.CreatedAt > Lifetime)
        {
            await cache.RemoveAsync(Key(id), ct);
            return null;
        }

        return stashed;
    }

    public async Task<StashedRequest?> ConsumeAsync(string? id, CancellationToken ct)
    {
        var stashed = await PeekAsync(id, ct);
        if (stashed is not null) await cache.RemoveAsync(Key(id!), ct);
        return stashed;
    }

    public async Task SaveAsync(string id, StashedRequest stashed, CancellationToken ct)
    {
        var remaining = stashed.CreatedAt + Lifetime - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return;

        await cache.SetStringAsync(Key(id), JsonSerializer.Serialize(stashed, Json),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remaining }, ct);
    }

    /// <summary>
    /// Rebuilds the original request as a query string, plus the stash id so the resumed pass can
    /// find its way back to the decision that was already made.
    /// </summary>
    public static string QueryString(string id, StashedRequest stashed)
    {
        var parameters = new Dictionary<string, string?>(stashed.Parameters!);
        parameters[Parameter] = id;

        return QueryHelpers.AddQueryString(string.Empty, parameters);
    }

    /// <summary>
    /// Whether a stash id presented on a resumed request really belongs to that request.
    /// </summary>
    public static bool Matches(StashedRequest stashed, OpenIddictRequest request) =>
        string.Equals(stashed.ClientId, request.ClientId, StringComparison.Ordinal)
        && string.Equals(stashed.RedirectUri, request.RedirectUri, StringComparison.Ordinal)
        && stashed.Scopes.ToHashSet(StringComparer.Ordinal).SetEquals(request.GetScopes());
}
