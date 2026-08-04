using System.Text.Json;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;

namespace Identity.Application.Services;

/// <summary>
/// Applies a partial update to a <see cref="UserPrivacySettings"/> row.
///
/// <para><b>Every field optional, omitted means "leave alone".</b> The alternative - a DTO with
/// nullable properties bound by the serializer - cannot tell "the client omitted this" from "the
/// client sent null", and for <c>dmRetentionDays</c> those are different requests: null is a
/// meaningful value meaning "keep forever". So the body is walked as JSON.</para>
///
/// <para><b>An unknown field is a 400, not an ignored key.</b> A client that misspells
/// <c>hidePushContent</c> and gets a 200 back believes it turned push previews off. Silently
/// dropping a privacy write is the failure mode this whole spec exists to remove, so the request is
/// refused whole - no field of it is applied.</para>
///
/// <para>Nothing is written to the entity until the entire body has validated, and only fields whose
/// value actually differs are reported as changed - which is what keeps <c>PATCH {}</c>, and a
/// re-post of the current state, from bumping the version and emitting a change event that every
/// consuming service would act on.</para>
/// </summary>
public static class PrivacySettingsPatch
{
    /// <summary>An upper bound on a user-set DM retention window. Twenty years is not a retention
    /// policy, and "forever" already has a representation (null).</summary>
    public const int MaxDmRetentionDays = 3650;

    /// <summary>
    /// The outcome of applying a patch.
    ///
    /// <para><c>RestrictedField</c> is set when the refusal was a minor-protection floor (T1-11)
    /// rather than a malformed body: it names the field the caller is not allowed to widen.
    /// Distinguished from an ordinary validation failure because the two get different status codes -
    /// <c>400</c> means "fix your request", <c>403 minor_restriction</c> means "this request is
    /// well-formed and you may not make it" - and a client that cannot tell them apart will retry the
    /// second one forever.</para>
    /// </summary>
    public sealed record Result(
        bool Ok,
        string? Error,
        IReadOnlyList<string> ChangedFields,
        string? RestrictedField = null)
    {
        public static Result Failed(string error) => new(false, error, []);
        public static Result Succeeded(IReadOnlyList<string> changed) => new(true, null, changed);
        public static Result Restricted(string field, string error) => new(false, error, [], field);
    }

    /// <summary>
    /// One writable field: how to read it out of the entity, how to write it back, and how to turn a
    /// JSON value into something assignable.
    ///
    /// <para>A table rather than a switch so the allow-list the unknown-field check runs against and
    /// the set of fields that can actually be written are the same object. Two lists that have to
    /// agree eventually stop agreeing, and the way that failure presents is a setting the API accepts
    /// and never stores.</para>
    /// </summary>
    private sealed record Field(
        string Name,
        Func<UserPrivacySettings, object?> Read,
        Action<UserPrivacySettings, object?> Write,
        Func<JsonElement, (object? Value, string? Error)> Parse);

    private static readonly Field[] Fields =
    [
        Bool("allowDataCollection", s => s.AllowDataCollection, (s, v) => s.AllowDataCollection = v),
        Bool("allowPersonalization", s => s.AllowPersonalization, (s, v) => s.AllowPersonalization = v),
        Bool("allowVoiceRecordingInClips", s => s.AllowVoiceRecordingInClips, (s, v) => s.AllowVoiceRecordingInClips = v),

        Enum<DirectMessagePolicy>("directMessagePolicy", s => s.DirectMessagePolicy, (s, v) => s.DirectMessagePolicy = v),
        Enum<FriendRequestPolicy>("friendRequestPolicy", s => s.FriendRequestPolicy, (s, v) => s.FriendRequestPolicy = v),

        Bool("discoverableByUsername", s => s.DiscoverableByUsername, (s, v) => s.DiscoverableByUsername = v),
        Bool("discoverableByEmail", s => s.DiscoverableByEmail, (s, v) => s.DiscoverableByEmail = v),
        Bool("discoverableByPhone", s => s.DiscoverableByPhone, (s, v) => s.DiscoverableByPhone = v),

        Enum<Visibility>("mutualServersVisibility", s => s.MutualServersVisibility, (s, v) => s.MutualServersVisibility = v),
        Enum<Visibility>("mutualFriendsVisibility", s => s.MutualFriendsVisibility, (s, v) => s.MutualFriendsVisibility = v),
        Enum<Visibility>("connectionsVisibility", s => s.ConnectionsVisibility, (s, v) => s.ConnectionsVisibility = v),
        Enum<Visibility>("birthdayVisibility", s => s.BirthdayVisibility, (s, v) => s.BirthdayVisibility = v),

        Bool("shareActivity", s => s.ShareActivity, (s, v) => s.ShareActivity = v),
        Bool("allowPositionalVoiceCapture", s => s.AllowPositionalVoiceCapture, (s, v) => s.AllowPositionalVoiceCapture = v),

        Bool("sendReadReceipts", s => s.SendReadReceipts, (s, v) => s.SendReadReceipts = v),
        Bool("sendTypingIndicators", s => s.SendTypingIndicators, (s, v) => s.SendTypingIndicators = v),

        new Field(
            "dmRetentionDays",
            s => s.DmRetentionDays,
            (s, v) => s.DmRetentionDays = (int?)v,
            ParseRetentionDays),

        Enum<ExplicitContentFilter>("explicitContentFilter", s => s.ExplicitContentFilter, (s, v) => s.ExplicitContentFilter = v),

        Bool("hidePushContent", s => s.HidePushContent, (s, v) => s.HidePushContent = v),
    ];

    private static readonly Dictionary<string, Field> ByName =
        Fields.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>The writable field names in the camelCase the API publishes, for error messages and
    /// for documentation tests.</summary>
    public static IReadOnlyList<string> WritableFieldNames { get; } = Fields.Select(f => f.Name).ToArray();

    /// <param name="body">The PATCH body, as received.</param>
    /// <param name="settings">The account's settings row, mutated in place once everything validates.</param>
    /// <param name="isMinor">
    /// When true, the minor-protection floors of T1-11 are applied to the staged values and a write
    /// that would breach one is refused whole - checked in the same all-or-nothing validation pass as
    /// everything else, so a body that pairs a permitted change with a forbidden one applies neither.
    /// Resolved by the caller from the account's birth date on every request; nothing here caches it.
    /// </param>
    public static Result Apply(JsonElement body, UserPrivacySettings settings, bool isMinor = false)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Result.Failed("The request body must be a JSON object.");

        // Pass one: validate everything. A patch is all-or-nothing, so no property may reach the
        // entity while a later one might still be refused.
        var staged = new List<(Field Field, object? Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in body.EnumerateObject())
        {
            if (!ByName.TryGetValue(property.Name, out var field))
            {
                return Result.Failed(
                    $"Unknown field '{property.Name}'. Known fields: {string.Join(", ", WritableFieldNames)}.");
            }

            if (!seen.Add(field.Name))
                return Result.Failed($"Field '{field.Name}' was supplied more than once.");

            var (value, error) = field.Parse(property.Value);
            if (error is not null) return Result.Failed(error);

            if (isMinor && MinorPrivacyFloors.Violates(field.Name, value))
            {
                return Result.Restricted(field.Name,
                    $"'{field.Name}' cannot be set to that value on an account below the age of "
                    + "majority. This restriction is applied by the server and lifts on its own when "
                    + "the account is old enough.");
            }

            staged.Add((field, value));
        }

        // Pass two: apply, recording only the fields whose value actually moved.
        var changed = new List<string>();
        foreach (var (field, value) in staged)
        {
            if (Equals(field.Read(settings), value)) continue;
            field.Write(settings, value);
            changed.Add(field.Name);
        }

        changed.Sort(StringComparer.Ordinal);
        return Result.Succeeded(changed);
    }

    // ── field constructors ──────────────────────────────────────────────────

    private static Field Bool(string name, Func<UserPrivacySettings, bool> read, Action<UserPrivacySettings, bool> write) =>
        new(name,
            s => read(s),
            (s, v) => write(s, (bool)v!),
            value => value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (value.GetBoolean(), null)
                // Null is refused rather than coerced to false: a client sending null for a consent
                // flag has a bug, and reading it as "withdraw consent" would be a guess about intent.
                : (null, $"{name} must be true or false."));

    private static Field Enum<TEnum>(string name, Func<UserPrivacySettings, TEnum> read, Action<UserPrivacySettings, TEnum> write)
        where TEnum : struct, System.Enum =>
        new(name,
            s => read(s),
            (s, v) => write(s, (TEnum)v!),
            value => ParseEnum<TEnum>(name, value));

    private static (object? Value, string? Error) ParseEnum<TEnum>(string name, JsonElement value)
        where TEnum : struct, System.Enum
    {
        if (value.ValueKind == JsonValueKind.String
            && System.Enum.TryParse<TEnum>(value.GetString(), ignoreCase: true, out var parsed)
            && System.Enum.IsDefined(parsed))
        {
            return (parsed, null);
        }

        // The numeric form is accepted because a client generated from the OpenAPI document may send
        // it. Range-checked, so an out-of-range int cannot become a policy value that no branch of
        // the enforcement table matches - which would fail *open* at every call site with a default
        // arm.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            var candidate = (TEnum)System.Enum.ToObject(typeof(TEnum), numeric);
            if (System.Enum.IsDefined(candidate)) return (candidate, null);
        }

        return (null, $"{name} must be one of: {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
    }

    private static (object? Value, string? Error) ParseRetentionDays(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return (null, null);

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var days))
            return (null, "dmRetentionDays must be a whole number of days, or null to keep messages forever.");

        if (days < 1 || days > MaxDmRetentionDays)
            return (null, $"dmRetentionDays must be between 1 and {MaxDmRetentionDays}, or null to keep messages forever.");

        return (days, null);
    }
}
