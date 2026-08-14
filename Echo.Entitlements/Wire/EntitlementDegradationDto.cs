using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Wire;

/// <summary>One reduction, as the client reads it.</summary>
public sealed record EntitlementDegradationDto
{
    /// <summary>
    /// The only constructor, and it validates, so an invalid degradation cannot be built at all -
    /// not by a call site, not by a deserialiser.
    /// </summary>
    [JsonConstructor]
    public EntitlementDegradationDto(
        string key,
        EntitlementValueDto requested,
        EntitlementValueDto granted,
        string reason,
        string remedy,
        bool actorCanRemedy,
        EntitlementSubjectDto subject,
        string? boundBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(subject);

        if (!EntitlementReasonCodes.IsKnown(reason))
        {
            throw new ArgumentException(
                $"'{reason}' is not an entitlement reason code. The vocabulary is closed because every "
                + $"code is a translation key in three clients: {string.Join(", ", EntitlementReasonCodes.All)}.",
                nameof(reason));
        }

        if (!EntitlementRemedyCodes.IsKnown(remedy))
        {
            throw new ArgumentException(
                $"'{remedy}' is not a remedy. Known: {string.Join(", ", EntitlementRemedyCodes.All)}.",
                nameof(remedy));
        }

        if (boundBy is not null && !EntitlementBoundBy.IsKnown(boundBy))
        {
            throw new ArgumentException(
                $"'{boundBy}' is not a side of a pair. It is '{EntitlementBoundBy.Guild}' or "
                + $"'{EntitlementBoundBy.User}'.", nameof(boundBy));
        }

        if (reason == EntitlementReasonCodes.PairedCeiling && boundBy is null)
        {
            throw new ArgumentException(
                "A paired ceiling must say which side bound. Without it the client cannot choose a call "
                + "to action and will eventually tell a paying member that their own plan limited them.",
                nameof(boundBy));
        }

        if (reason == EntitlementReasonCodes.OperatorCeiling && remedy != EntitlementRemedyCodes.None)
        {
            throw new ArgumentException(
                "An operator ceiling is what this box will do, not what anybody has paid for, so it has "
                + "no remedy. Offering an upgrade against one sells a change that would not happen.",
                nameof(remedy));
        }

        if (remedy == EntitlementRemedyCodes.None && actorCanRemedy)
        {
            throw new ArgumentException(
                "There is no remedy, so nobody can perform it.", nameof(actorCanRemedy));
        }

        Key = key;
        Requested = requested;
        Granted = granted;
        Reason = reason;
        Remedy = remedy;
        ActorCanRemedy = actorCanRemedy;
        Subject = subject;
        BoundBy = boundBy;
    }

    /// <summary>The entitlement key that bound, as named in the catalogue.</summary>
    public string Key { get; }

    public EntitlementValueDto Requested { get; }

    public EntitlementValueDto Granted { get; }

    public string Reason { get; }

    /// <summary>Present whenever a subject bound: always for a paired ceiling, and for the two
    /// single-sided plan limits because saying so costs nothing and lets one client code path handle
    /// all three. Absent for an operator ceiling, which is not a subject anyone can upgrade.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundBy { get; }

    public string Remedy { get; }

    public bool ActorCanRemedy { get; }

    /// <summary>Whose limit this was.</summary>
    public EntitlementSubjectDto Subject { get; }

    /// <summary>The wire form of a degradation an enforcement site already produced.</summary>
    public static EntitlementDegradationDto From(
        EntitlementDegradation degradation,
        EntitlementKey key,
        EntitlementSubject subject,
        EntitlementRemedyDecision remedy,
        string? boundBy = null)
    {
        ArgumentNullException.ThrowIfNull(degradation);
        ArgumentNullException.ThrowIfNull(key);

        if (!string.Equals(degradation.Key, key.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The degradation is about '{degradation.Key}' but was given the key '{key.Name}'.", nameof(key));
        }

        return From(
            key,
            key.Parse(degradation.Requested),
            key.Parse(degradation.Granted),
            degradation.Reason,
            subject,
            remedy,
            boundBy);
    }

    /// <summary>The wire form built from the two values directly, for a site that decided without
    /// going through <see cref="EntitlementDegradation.IfReduced"/>.</summary>
    public static EntitlementDegradationDto From(
        EntitlementKey key,
        EntitlementValue requested,
        EntitlementValue granted,
        EntitlementDegradationReason reason,
        EntitlementSubject subject,
        EntitlementRemedyDecision remedy,
        string? boundBy = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        return new EntitlementDegradationDto(
            key.Name,
            EntitlementValueDto.From(key, requested),
            EntitlementValueDto.From(key, granted),
            EntitlementReasonCodes.Of(reason),
            remedy.Remedy,
            remedy.ActorCanRemedy,
            EntitlementSubjectDto.From(subject),
            boundBy ?? DefaultBoundBy(reason));
    }

    /// <summary>The side implied by a single-sided reason.</summary>
    private static string? DefaultBoundBy(EntitlementDegradationReason reason) => reason switch
    {
        EntitlementDegradationReason.GuildPlanLimit => EntitlementBoundBy.Guild,
        EntitlementDegradationReason.UserPlanLimit => EntitlementBoundBy.User,
        _ => null,
    };
}

/// <summary>
/// A refusal, for the three things spec section 3.3 says cannot degrade: the 51st emoji, the
/// oversized upload, the 6th bot.
/// </summary>
public sealed record EntitlementDenialDto
{
    /// <summary>The status every entitlement refusal is served with.</summary>
    public const int StatusCode = 403;

    [JsonConstructor]
    public EntitlementDenialDto(
        string code,
        string key,
        string reason,
        string remedy,
        bool actorCanRemedy,
        EntitlementSubjectDto subject,
        EntitlementValueDto? requested = null,
        EntitlementValueDto? granted = null,
        string? boundBy = null,
        string? feature = null,
        bool retryable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(subject);

        if (!EntitlementReasonCodes.IsKnown(code) || !string.Equals(code, reason, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A denial's code is its reason code. They are one vocabulary, and a second one would be "
                + "the same sentence written twice.", nameof(code));
        }

        if (!EntitlementRemedyCodes.IsKnown(remedy))
        {
            throw new ArgumentException(
                $"'{remedy}' is not a remedy. Known: {string.Join(", ", EntitlementRemedyCodes.All)}.",
                nameof(remedy));
        }

        if (boundBy is not null && !EntitlementBoundBy.IsKnown(boundBy))
        {
            throw new ArgumentException($"'{boundBy}' is not a side of a pair.", nameof(boundBy));
        }

        if (reason == EntitlementReasonCodes.PairedCeiling && boundBy is null)
        {
            throw new ArgumentException(
                "A paired ceiling must say which side bound, refusal or reduction alike.", nameof(boundBy));
        }

        if (reason == EntitlementReasonCodes.OperatorCeiling && remedy != EntitlementRemedyCodes.None)
        {
            throw new ArgumentException("An operator ceiling has no remedy.", nameof(remedy));
        }

        if (remedy == EntitlementRemedyCodes.None && actorCanRemedy)
        {
            throw new ArgumentException("There is no remedy, so nobody can perform it.", nameof(actorCanRemedy));
        }

        Code = code;
        Key = key;
        Reason = reason;
        Remedy = remedy;
        ActorCanRemedy = actorCanRemedy;
        Subject = subject;
        Requested = requested;
        Granted = granted;
        BoundBy = boundBy;
        Feature = feature;
        Retryable = retryable;
    }

    /// <summary>The one field name for the code.</summary>
    public string Code { get; }

    public string Key { get; }

    /// <summary>Null for a refusal with no countable ceiling - an out-of-plan module, where the thing
    /// being refused is a feature and not a number.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EntitlementValueDto? Requested { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EntitlementValueDto? Granted { get; }

    public string Reason { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundBy { get; }

    public string Remedy { get; }

    public bool ActorCanRemedy { get; }

    public EntitlementSubjectDto Subject { get; }

    /// <summary>The <c>GuildFeatures</c> name, when what was refused is a module the plan does not
    /// include. Present so the client can name the module in its own copy without parsing
    /// <see cref="Key"/>, and absent everywhere else.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Feature { get; }

    /// <summary>Always false here, and present because the clients' refusal renderer reads it. An
    /// entitlement refusal is not a transient failure and retrying it is how a client turns one
    /// refusal into three.</summary>
    public bool Retryable { get; }

    /// <summary>The refusal that corresponds to a reduction that could not be made.</summary>
    public static EntitlementDenialDto From(EntitlementDegradationDto degradation)
    {
        ArgumentNullException.ThrowIfNull(degradation);

        return new EntitlementDenialDto(
            degradation.Reason,
            degradation.Key,
            degradation.Reason,
            degradation.Remedy,
            degradation.ActorCanRemedy,
            degradation.Subject,
            degradation.Requested,
            degradation.Granted,
            degradation.BoundBy);
    }
}

/// <summary>What would fix a limit and whether this caller can do it.</summary>
public readonly record struct EntitlementRemedyDecision(string Remedy, bool ActorCanRemedy)
{
    public static readonly EntitlementRemedyDecision None = new(EntitlementRemedyCodes.None, false);
}

/// <summary>The "who can fix this" rule, in one place.</summary>
public static class EntitlementRemedyPolicy
{
    /// <param name="instanceSellsUpgrades">False on a self-hosted instance, and false on a hosted one
    /// with no billing configured. Both render as a sentence with no button: an upgrade link to a
    /// service that is not deployed is worse than no link.</param>
    /// <param name="actorCanManageGuild">Whether this caller could actually buy the guild's upgrade.
    /// Ignored for a user-side limit, where the caller is always the person who would upgrade.</param>
    public static EntitlementRemedyDecision For(
        EntitlementDegradationReason reason,
        string? boundBy,
        bool instanceSellsUpgrades,
        bool actorCanManageGuild)
    {
        if (!instanceSellsUpgrades) return EntitlementRemedyDecision.None;

        return reason switch
        {
            EntitlementDegradationReason.OperatorCeiling => EntitlementRemedyDecision.None,
            EntitlementDegradationReason.GuildPlanLimit => Guild(actorCanManageGuild),
            EntitlementDegradationReason.UserPlanLimit => User(),
            EntitlementDegradationReason.PairedCeiling when boundBy == EntitlementBoundBy.User => User(),
            EntitlementDegradationReason.PairedCeiling when boundBy == EntitlementBoundBy.Guild =>
                Guild(actorCanManageGuild),
            EntitlementDegradationReason.PairedCeiling => throw new ArgumentException(
                "A paired ceiling has no remedy until it says which side bound.", nameof(boundBy)),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown degradation reason."),
        };
    }

    private static EntitlementRemedyDecision Guild(bool actorCanManageGuild) =>
        new(EntitlementRemedyCodes.UpgradeGuild, actorCanManageGuild);

    private static EntitlementRemedyDecision User() =>
        new(EntitlementRemedyCodes.UpgradeUser, true);
}

/// <summary>Attaching <c>degradations</c> to a response that already exists.</summary>
public static class EntitlementResponses
{
    public const string PropertyName = "degradations";

    /// <summary>The body with <c>degradations</c> attached, or the body unchanged when nothing was
    /// reduced. An empty array is never written: absent and empty mean the same thing to a client,
    /// and absent is what a v1 response looked like.</summary>
    public static JsonNode? WithDegradations<T>(
        T body,
        IReadOnlyList<EntitlementDegradationDto>? degradations,
        JsonSerializerOptions? options = null)
    {
        var node = JsonSerializer.SerializeToNode(body, options ?? DefaultOptions);

        if (degradations is null || degradations.Count == 0) return node;

        if (node is not JsonObject json)
        {
            throw new InvalidOperationException(
                "Degradations ride the response body, so the body has to be a JSON object. A bare array "
                + "or scalar has nowhere to carry them and needs an enveloping object first.");
        }

        json[PropertyName] = JsonSerializer.SerializeToNode(degradations, options ?? DefaultOptions);
        return json;
    }

    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);
}
