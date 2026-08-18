using AppEnvironment;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>The message-length ceiling that applies to one send, and who set it.</summary>
/// <param name="MaxCharacters">Text elements, never bytes - see <c>MessageLength.Of</c>.</param>
/// <param name="Key">The entitlement key that bound, which decides the upgrade surface a client shows.</param>
/// <param name="Subject">Whose limit this was.</param>
/// <param name="Reason">Which side bound, in the shared reason vocabulary.</param>
public sealed record MessageLengthLimit(
    int MaxCharacters,
    EntitlementKey Key,
    EntitlementSubject Subject,
    EntitlementDegradationReason Reason)
{
    /// <summary>
    /// How much longer an encrypted body may be than the ceiling. The server cannot count the
    /// characters in something it cannot read, and the armoured ciphertext of a post at the limit is
    /// several times its length once UTF-8, base64 and MLS framing have had their turn. Measuring it
    /// against the plain ceiling would refuse legitimate posts, so this is a sanity bound on the
    /// envelope rather than the ceiling itself.
    /// </summary>
    public const int CiphertextAllowance = 8;

    /// <summary>The ceiling actually applied to a body of this kind.</summary>
    /// <param name="encrypted">Whether the body is ciphertext rather than readable text.</param>
    /// <returns>The maximum permitted length, in text elements.</returns>
    public int EffectiveMaximum(bool encrypted) =>
        encrypted ? MaxCharacters * CiphertextAllowance : MaxCharacters;

    /// <summary>True when a body of this length may not be posted.</summary>
    /// <param name="length">The measured length, in text elements.</param>
    /// <param name="encrypted">Whether the body is ciphertext.</param>
    /// <returns>Whether the send must be refused.</returns>
    public bool Exceeds(int length, bool encrypted = false) => length > EffectiveMaximum(encrypted);
}

/// <summary>
/// The one place that answers "how long may this message be": <c>guild.message_max_length</c> in a
/// guild channel and <c>user.message_max_length</c> everywhere else, clamped by this instance's own
/// hard ceiling.
/// </summary>
public sealed class MessageLengthPolicy(
    IMessageBus bus,
    IDistributedCache cache,
    ILogger<MessageLengthPolicy> logger,
    EntitlementResolver? entitlements = null,
    OperatorCeilings? operatorCeilings = null)
{
    /// <summary>
    /// The absolute ceiling. No plan, no admin grant, no operator configuration and no
    /// misconfiguration produces a number above this: these bodies are stored in Scylla and read
    /// back whole on every history page, and the abuse case is a single request that costs the
    /// cluster megabytes.
    /// </summary>
    public const int HardCeilingCharacters = 15_000;

    /// <summary>The <c>error</c> code on a refusal, in the same vocabulary as the auto-mod and
    /// slowmode refusals this endpoint already emits.</summary>
    public const string TooLongError = "message_too_long";

    /// <summary>
    /// 413 rather than the 403 the entitlement contract uses for a hard denial, and rather than a
    /// bare 400. The clients treat 403 as "you are not allowed in here", which this is not - the
    /// author may post, just not this much - and 413 is the one status that says exactly what
    /// happened without a body having to explain it. Both create and edit answer with it.
    /// </summary>
    public const int TooLongStatusCode = 413;

    private readonly OperatorCeilings _operatorCeilings = operatorCeilings ?? OperatorCeilings.None;

    /// <summary>The ceiling for a send into either kind of context.</summary>
    /// <param name="channelId">The guild channel, or null for a conversation.</param>
    /// <param name="conversationId">The DM or group, or null for a channel.</param>
    /// <param name="userId">Who is sending.</param>
    /// <param name="cancellationToken">Cancels the entitlement and channel lookups.</param>
    /// <returns>The effective ceiling.</returns>
    public async Task<MessageLengthLimit> ForAsync(
        string? channelId, string? conversationId, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return await ForUserAsync(userId, cancellationToken);

        var guildId = await GuildIdForChannelAsync(channelId, cancellationToken);

        // A channel whose guild cannot be named - Guild is down, or the channel has been deleted
        // out from under a client - falls to the hard ceiling rather than to the free tier. Refusing
        // a long post because an unrelated service is unreachable would turn a soft limit into an
        // availability dependency on the hottest path in the product.
        if (guildId is null)
        {
            return new MessageLengthLimit(
                HardCeilingCharacters,
                EntitlementKeys.GuildMessageMaxLength,
                EntitlementSubject.ForUser(userId),
                EntitlementDegradationReason.OperatorCeiling);
        }

        var subject = EntitlementSubject.ForGuild(guildId);

        return Clamp(
            EntitlementKeys.GuildMessageMaxLength,
            await ResolveAsync(subject, EntitlementKeys.GuildMessageMaxLength, cancellationToken),
            subject,
            EntitlementDegradationReason.GuildPlanLimit);
    }

    /// <summary>The ceiling where no guild is involved.</summary>
    /// <param name="userId">Whose plan decides.</param>
    /// <param name="cancellationToken">Cancels the entitlement lookup.</param>
    /// <returns>The effective ceiling.</returns>
    public async Task<MessageLengthLimit> ForUserAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var subject = EntitlementSubject.ForUser(userId);

        return Clamp(
            EntitlementKeys.UserMessageMaxLength,
            await ResolveAsync(subject, EntitlementKeys.UserMessageMaxLength, cancellationToken),
            subject,
            EntitlementDegradationReason.UserPlanLimit);
    }

    /// <summary>The refusal a client can act on, in the shared entitlement vocabulary.</summary>
    /// <param name="limit">The ceiling that bound.</param>
    /// <param name="actualLength">How long the body actually was.</param>
    /// <param name="encrypted">Whether the body was ciphertext, which has its own allowance.</param>
    /// <param name="userId">Who was refused, for the "can this person fix it" decision.</param>
    /// <param name="cancellationToken">Cancels the permission lookup behind the remedy.</param>
    /// <returns>A <see cref="TooLongStatusCode"/> carrying the limit and the length that exceeded it.</returns>
    public async Task<IResult> RefuseAsync(
        MessageLengthLimit limit,
        int actualLength,
        bool encrypted,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var sellsUpgrades = Env.License.IsHosted && Env.License.IsBillingConfigured;

        var canManageGuild = sellsUpgrades
            && limit.Reason == EntitlementDegradationReason.GuildPlanLimit
            && limit.Subject.Kind == SubjectKind.Guild
            && await CanManageGuildAsync(limit.Subject.Id, userId, cancellationToken);

        var maxLength = limit.EffectiveMaximum(encrypted);

        var degradation = EntitlementDegradationDto.From(
            limit.Key,
            EntitlementValue.OfNumber(actualLength),
            EntitlementValue.OfNumber(maxLength),
            limit.Reason,
            limit.Subject,
            EntitlementRemedyPolicy.For(limit.Reason, null, sellsUpgrades, canManageGuild));

        // Both halves in one body: the two numbers a client needs to write "this server allows
        // 4,000 characters and this is 4,180", and the entitlement denial that says whether an
        // upgrade would lift it and who would have to buy it.
        return Results.Json(
            new
            {
                error = TooLongError,
                maxLength,
                length = actualLength,
                entitlement = EntitlementDenialDto.From(degradation),
            },
            statusCode: TooLongStatusCode);
    }

    /// <summary>
    /// The entitlement, clamped by the operator's ceiling and then by this instance's own. An
    /// unlimited answer means nothing was configured, which is the normal state of a self-hosted
    /// instance rather than an edge case - it lands on the hard ceiling, which is the most generous
    /// number this instance will store, not on zero and not below the free tier.
    /// </summary>
    private MessageLengthLimit Clamp(
        EntitlementKey key,
        EntitlementValue entitled,
        EntitlementSubject subject,
        EntitlementDegradationReason planReason)
    {
        var clamped = _operatorCeilings.Clamp(key, entitled).AsNumber;

        if (clamped == EntitlementValue.Unlimited || clamped > HardCeilingCharacters)
        {
            return new MessageLengthLimit(
                HardCeilingCharacters, key, subject, EntitlementDegradationReason.OperatorCeiling);
        }

        var reason = _operatorCeilings.Binds(key, entitled)
            ? EntitlementDegradationReason.OperatorCeiling
            : planReason;

        return new MessageLengthLimit((int)clamped, key, subject, reason);
    }

    private async Task<EntitlementValue> ResolveAsync(
        EntitlementSubject subject, EntitlementKey key, CancellationToken cancellationToken)
    {
        if (entitlements is null) return key.Default;

        var resolved = await entitlements.ResolveAsync(subject, cancellationToken);
        return resolved.Value(key);
    }

    private static string GuildCacheKey(string channelId) => $"messaging:channel-guild:{channelId}";

    /// <summary>Which guild a channel belongs to, cached because the send path asks on every post.</summary>
    private async Task<string?> GuildIdForChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var cacheKey = GuildCacheKey(channelId);

        try
        {
            var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
            if (cached is not null) return cached.Length == 0 ? null : cached;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Channel-to-guild cache unreadable for {ChannelId}", channelId);
        }

        string? guildId;

        try
        {
            var response = await bus.InvokeAsync<GetChannelResponse>(
                new GetChannelRequest { ChannelId = channelId }, cancellationToken);
            guildId = response.Channel?.GuildId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Guild unresolvable for channel {ChannelId}", channelId);
            return null;
        }

        try
        {
            // A channel never moves between guilds, so the hit is cached for an hour rather than the
            // five minutes the auto-mod config uses; that one is edited from a settings screen and
            // this one is immutable. A miss is cached briefly so a channel created a moment ago is
            // not answered as guildless for the rest of the hour.
            await cache.SetStringAsync(cacheKey, guildId ?? string.Empty, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = guildId is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1),
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Channel-to-guild cache unwritable for {ChannelId}", channelId);
        }

        return guildId;
    }

    private async Task<bool> CanManageGuildAsync(
        string guildId, string userId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await bus.InvokeAsync<HasUserPermissionToGuildResponse>(
                new HasUserPermissionToGuildRequest
                {
                    GuildId = guildId,
                    UserId = userId,
                    Permission = ExternalPermission.ManageGuild,
                }, cancellationToken);

            return response.IsAllowed;
        }
        catch (Exception ex)
        {
            // Only decides whether the refusal carries an upgrade button.
            logger.LogWarning(ex, "ManageGuild unresolvable for {UserId} in {GuildId}", userId, guildId);
            return false;
        }
    }
}
