using System.Text.Json;
using System.Text.Json.Serialization;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Caching;

/// <summary>What came back out of the cache: the set, and the instant it stops being servable
/// without re-resolving.</summary>
public readonly record struct CachedEntitlementSet(EntitlementSet Set, DateTimeOffset FreshUntil)
{
    public bool IsFreshAt(DateTimeOffset instant) => instant < FreshUntil;
}

/// <summary>Turns a resolved set into bytes and back.</summary>
public sealed class EntitlementSetCodec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, EntitlementKey> _byName;

    /// <param name="catalogue">Defaults to <see cref="EntitlementKeys.All"/>.</param>
    public EntitlementSetCodec(IReadOnlyList<EntitlementKey>? catalogue = null) =>
        _byName = (catalogue ?? EntitlementKeys.All).ToDictionary(key => key.Name, StringComparer.Ordinal);

    public string Encode(EntitlementSet set, DateTimeOffset freshUntil)
    {
        ArgumentNullException.ThrowIfNull(set);

        var entries = new List<CacheEntryPayload>(set.Count);

        foreach (var entry in set.Entries)
        {
            entries.Add(new CacheEntryPayload
            {
                Key = entry.Key.Name,
                Kind = entry.Value.Kind.ToString(),
                Raw = entry.Value.Raw,
                Source = entry.Provenance.Source.ToString(),
                Detail = entry.Provenance.Detail,
            });
        }

        return JsonSerializer.Serialize(
            new CacheSetPayload { FreshUntil = freshUntil.ToUnixTimeMilliseconds(), Entries = entries },
            Json);
    }

    /// <summary>The set a payload holds, or null when it cannot be read at all.</summary>
    public CachedEntitlementSet? Decode(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        CacheSetPayload? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<CacheSetPayload>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null) return null;

        var entries = new Dictionary<EntitlementKey, EntitlementEntry>();

        foreach (var row in parsed.Entries)
        {
            if (row.Key is null || !_byName.TryGetValue(row.Key, out var key)) continue;
            if (!Enum.TryParse<EntitlementValueKind>(row.Kind, out var kind) || kind != key.ValueKind) continue;
            if (!Enum.TryParse<EntitlementPrecedence>(row.Source, out var source)) continue;
            if (!TryValue(kind, row.Raw, out var value)) continue;

            entries[key] = new EntitlementEntry(key, value, new EntitlementProvenance(source, row.Detail));
        }

        return new CachedEntitlementSet(
            new EntitlementSet(entries), DateTimeOffset.FromUnixTimeMilliseconds(parsed.FreshUntil));
    }

    /// <summary>
    /// Rebuilds a value from its raw payload, refusing the ones the value type itself would refuse.
    /// </summary>
    private static bool TryValue(EntitlementValueKind kind, long raw, out EntitlementValue value)
    {
        value = default;

        if (raw < 0) return false;

        value = kind switch
        {
            EntitlementValueKind.Flag => EntitlementValue.OfFlag(raw != 0),
            EntitlementValueKind.Numeric => EntitlementValue.OfNumber(raw),
            EntitlementValueKind.Ladder when raw <= int.MaxValue => EntitlementValue.OfRank((int)raw),
            _ => default,
        };

        return value.Kind == kind;
    }

    /// <summary>Short property names because this is written on every resolution and read on every
    /// request, and long ones would be most of the payload. It is still readable in <c>redis-cli</c>,
    /// which is the only other audience it has.</summary>
    private sealed class CacheSetPayload
    {
        [JsonPropertyName("f")]
        public long FreshUntil { get; set; }

        [JsonPropertyName("e")]
        public List<CacheEntryPayload> Entries { get; set; } = [];
    }

    private sealed class CacheEntryPayload
    {
        [JsonPropertyName("k")]
        public string? Key { get; set; }

        [JsonPropertyName("t")]
        public string? Kind { get; set; }

        [JsonPropertyName("v")]
        public long Raw { get; set; }

        [JsonPropertyName("s")]
        public string? Source { get; set; }

        [JsonPropertyName("d")]
        public string? Detail { get; set; }
    }
}
