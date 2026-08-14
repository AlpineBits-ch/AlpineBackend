using System.Text.Json;
using System.Text.Json.Serialization;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Wire;

/// <summary>
/// The client-facing vocabulary: the codes, the value shapes and the version of the whole set.
/// </summary>
public static class EntitlementContract
{
    /// <summary>Bumped when a code is added to any vocabulary in this file.</summary>
    public const int VocabularyVersion = 1;
}

/// <summary>Why a request was reduced or refused, as it crosses the wire.</summary>
public static class EntitlementReasonCodes
{
    public const string GuildPlanLimit = "guild_plan_limit";
    public const string UserPlanLimit = "user_plan_limit";
    public const string PairedCeiling = "paired_ceiling";
    public const string OperatorCeiling = "operator_ceiling";

    /// <summary>Every code the server may emit.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        GuildPlanLimit,
        UserPlanLimit,
        PairedCeiling,
        OperatorCeiling,
    ];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);

    public static string Of(EntitlementDegradationReason reason) => reason switch
    {
        EntitlementDegradationReason.GuildPlanLimit => GuildPlanLimit,
        EntitlementDegradationReason.UserPlanLimit => UserPlanLimit,
        EntitlementDegradationReason.PairedCeiling => PairedCeiling,
        EntitlementDegradationReason.OperatorCeiling => OperatorCeiling,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason,
            "This degradation reason has no wire code. Adding a reason means adding a code here, a "
            + "translation key in every client and a bump of EntitlementContract.VocabularyVersion."),
    };
}

/// <summary>What would fix it, which is what the button says.</summary>
public static class EntitlementRemedyCodes
{
    public const string UpgradeGuild = "upgrade_guild";
    public const string UpgradeUser = "upgrade_user";

    /// <summary>Reserved now and emitted by nobody yet.</summary>
    public const string BoostGuild = "boost_guild";

    /// <summary>Nothing can be bought that changes this.</summary>
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [UpgradeGuild, UpgradeUser, BoostGuild, None];

    public static bool IsKnown(string? remedy) => remedy is not null && All.Contains(remedy, StringComparer.Ordinal);
}

/// <summary>Which side of a pair actually bound.</summary>
public static class EntitlementBoundBy
{
    public const string Guild = "guild";
    public const string User = "user";

    public static bool IsKnown(string? boundBy) =>
        boundBy is Guild or User;

    public static string Of(SubjectKind kind) => kind switch
    {
        SubjectKind.Guild => Guild,
        SubjectKind.User => User,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown subject kind."),
    };
}

/// <summary>The two subject kinds as the client spells them.</summary>
public static class EntitlementSubjectKinds
{
    public const string User = "user";
    public const string Guild = "guild";

    public static string Of(SubjectKind kind) => kind switch
    {
        SubjectKind.User => User,
        SubjectKind.Guild => Guild,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown subject kind."),
    };
}

/// <summary>Whose entitlements these are, echoed on everything.</summary>
public sealed record EntitlementSubjectDto(string Kind, string Id)
{
    public static EntitlementSubjectDto From(EntitlementSubject subject) =>
        new(EntitlementSubjectKinds.Of(subject.Kind), subject.Id);
}

/// <summary>
/// One entitlement value on the wire, in whichever of the three shapes its key declares.
/// </summary>
[JsonConverter(typeof(EntitlementValueDtoConverter))]
public sealed record EntitlementValueDto
{
    public const string FlagKind = "flag";
    public const string NumericKind = "numeric";
    public const string LadderKind = "ladder";

    private EntitlementValueDto(string kind) => Kind = kind;

    /// <summary><see cref="FlagKind"/>, <see cref="NumericKind"/> or <see cref="LadderKind"/>. The
    /// client switches on this before reading anything else.</summary>
    public string Kind { get; }

    /// <summary>The limit, for a numeric value that has one.</summary>
    public long? Value { get; private init; }

    /// <summary>Read this before <see cref="Value"/>. Numeric values only.</summary>
    public bool Unlimited { get; private init; }

    public bool? Granted { get; private init; }

    public string? Rung { get; private init; }

    /// <summary>Position on the ladder, ascending.</summary>
    public int? Rank { get; private init; }

    public string? Ladder { get; private init; }

    public static EntitlementValueDto Flag(bool granted) =>
        new(FlagKind) { Granted = granted };

    public static EntitlementValueDto Number(long limit) =>
        limit == EntitlementValue.Unlimited
            ? new EntitlementValueDto(NumericKind) { Unlimited = true }
            : new EntitlementValueDto(NumericKind) { Value = limit };

    public static EntitlementValueDto Unbounded() => Number(EntitlementValue.Unlimited);

    public static EntitlementValueDto OnLadder(EntitlementLadder ladder, int rank)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        return OnLadder(ladder.Name, ladder.RungAt(rank), rank);
    }

    /// <summary>A ladder value assembled from what arrived on the wire, where there is a rung name
    /// and a rank but no ladder to check them against. Internal because producing one of these
    /// without a ladder is only ever the deserialiser's business.</summary>
    internal static EntitlementValueDto OnLadder(string? ladder, string rung, int rank) =>
        new(LadderKind)
        {
            Rung = rung,
            Rank = rank,
            Ladder = ladder,
        };

    /// <summary>The one conversion from the domain value.</summary>
    public static EntitlementValueDto From(EntitlementKey key, EntitlementValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (value.Kind != key.ValueKind)
        {
            throw new ArgumentException(
                $"Key '{key.Name}' is {key.ValueKind}; a {value.Kind} value was supplied.", nameof(value));
        }

        return key.ValueKind switch
        {
            EntitlementValueKind.Flag => Flag(value.AsFlag),
            EntitlementValueKind.Numeric => Number(value.AsNumber),
            EntitlementValueKind.Ladder => OnLadder(key.Ladder!, value.AsRank),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.ValueKind, "Unknown value kind."),
        };
    }

    /// <summary>Reads the value back, for a caller that holds the key.</summary>
    public EntitlementValue ToDomain(EntitlementKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Kind switch
        {
            FlagKind => EntitlementValue.OfFlag(Granted ?? false),
            NumericKind => EntitlementValue.OfNumber(Unlimited ? EntitlementValue.Unlimited : Value ?? 0),
            LadderKind => EntitlementValue.OfRank(Rank ?? key.Ladder!.RankOf(Rung!)),
            _ => throw new InvalidOperationException($"'{Kind}' is not an entitlement value kind."),
        };
    }
}

/// <summary>Writes the three shapes by hand.</summary>
public sealed class EntitlementValueDtoConverter : JsonConverter<EntitlementValueDto>
{
    public override void Write(Utf8JsonWriter writer, EntitlementValueDto value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);

        switch (value.Kind)
        {
            case EntitlementValueDto.NumericKind:
                if (value.Value == EntitlementValue.Unlimited)
                {
                    throw new InvalidOperationException(
                        "An entitlement limit of long.MaxValue was about to be written as a number. "
                        + "It exceeds JavaScript's Number.MAX_SAFE_INTEGER and every JS client would "
                        + "silently read a different value. Unlimited is carried as unlimited:true "
                        + "with a null limit.");
                }

                if (value.Unlimited) writer.WriteNull("value");
                else writer.WriteNumber("value", value.Value ?? 0);

                writer.WriteBoolean("unlimited", value.Unlimited);
                break;

            case EntitlementValueDto.FlagKind:
                writer.WriteBoolean("granted", value.Granted ?? false);
                break;

            case EntitlementValueDto.LadderKind:
                writer.WriteString("rung", value.Rung);
                writer.WriteNumber("rank", value.Rank ?? 0);
                if (value.Ladder is not null) writer.WriteString("ladder", value.Ladder);
                break;

            default:
                throw new InvalidOperationException($"'{value.Kind}' is not an entitlement value kind.");
        }

        writer.WriteEndObject();
    }

    public override EntitlementValueDto Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("An entitlement value is an object with a 'kind'.");
        }

        string? kind = null;
        long? number = null;
        var unlimited = false;
        bool? granted = null;
        string? rung = null;
        int? rank = null;
        string? ladder = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "kind": kind = reader.GetString(); break;
                case "value": number = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt64(); break;
                case "unlimited": unlimited = reader.GetBoolean(); break;
                case "granted": granted = reader.GetBoolean(); break;
                case "rung": rung = reader.GetString(); break;
                case "rank": rank = reader.GetInt32(); break;
                case "ladder": ladder = reader.GetString(); break;
                default: reader.Skip(); break;
            }
        }

        return kind switch
        {
            EntitlementValueDto.NumericKind => unlimited
                ? EntitlementValueDto.Unbounded()
                : EntitlementValueDto.Number(number ?? 0),
            EntitlementValueDto.FlagKind => EntitlementValueDto.Flag(granted ?? false),
            EntitlementValueDto.LadderKind when rung is not null && rank is not null =>
                EntitlementValueDto.OnLadder(ladder, rung, rank.Value),
            _ => throw new JsonException($"'{kind}' is not an entitlement value kind."),
        };
    }
}
