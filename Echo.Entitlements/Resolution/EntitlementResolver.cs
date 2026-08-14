using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;

namespace Echo.Entitlements.Resolution;

/// <summary>
/// Merges every source into one answer, by the rules in spec sections 4.1 and 4.2.
/// </summary>
public sealed class EntitlementResolver
{
    private readonly IReadOnlyList<IEntitlementSource> _sources;
    private readonly IReadOnlyList<EntitlementKey> _catalogue;

    /// <param name="sources">Consulted in precedence order; registration order is irrelevant.</param>
    /// <param name="catalogue">Defaults to <see cref="EntitlementKeys.All"/>. Overridable so a test
    /// can exercise a rule against one key rather than against the whole table.</param>
    public EntitlementResolver(
        IEnumerable<IEntitlementSource> sources,
        IReadOnlyList<EntitlementKey>? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _sources = sources.OrderBy(source => source.Precedence).ToList();
        _catalogue = catalogue ?? EntitlementKeys.All;
    }

    /// <summary>
    /// One subject's own entitlements: every key of its scope, plus its side of every paired key.
    /// </summary>
    public async Task<EntitlementSet> ResolveAsync(
        EntitlementSubject subject, CancellationToken cancellationToken = default)
    {
        var accumulated = new Dictionary<EntitlementKey, EntitlementEntry>();

        foreach (var source in _sources)
        {
            var contributed = await source.ResolveAsync(subject, cancellationToken).ConfigureAwait(false);

            foreach (var entry in contributed.Entries)
            {
                if (!entry.Key.AppliesTo(subject.Kind)) continue;

                accumulated[entry.Key] = accumulated.TryGetValue(entry.Key, out var existing)
                    ? Combine(existing, entry)
                    : entry;
            }

            if (source.ShortCircuits) break;
        }

        foreach (var key in _catalogue)
        {
            if (!key.AppliesTo(subject.Kind) || accumulated.ContainsKey(key)) continue;
            accumulated[key] = new EntitlementEntry(key, key.Default, EntitlementProvenance.CatalogueDefault);
        }

        return new EntitlementSet(accumulated);
    }

    /// <summary>What one member may actually do in one guild.</summary>
    public async Task<EntitlementSet> ResolveEffectiveAsync(
        EntitlementSubject guild, EntitlementSubject user, CancellationToken cancellationToken = default)
    {
        if (guild.Kind != SubjectKind.Guild)
        {
            throw new ArgumentException(
                "The first argument is the guild side of the pair. It was given a "
                + $"{guild.Kind} subject, which would invert every paired key.", nameof(guild));
        }

        if (user.Kind != SubjectKind.User)
        {
            throw new ArgumentException(
                "The second argument is the user side of the pair. It was given a "
                + $"{user.Kind} subject, which would invert every paired key.", nameof(user));
        }

        var guildSet = await ResolveAsync(guild, cancellationToken).ConfigureAwait(false);
        var userSet = await ResolveAsync(user, cancellationToken).ConfigureAwait(false);

        var effective = new Dictionary<EntitlementKey, EntitlementEntry>();

        foreach (var key in _catalogue)
        {
            effective[key] = key.Scope switch
            {
                EntitlementScope.Guild => Entry(guildSet, key),
                EntitlementScope.User => Entry(userSet, key),
                EntitlementScope.Paired => Restrict(Entry(guildSet, key), Entry(userSet, key)),
                _ => new EntitlementEntry(key, key.Default, EntitlementProvenance.CatalogueDefault),
            };
        }

        return new EntitlementSet(effective);
    }

    private static EntitlementEntry Entry(EntitlementSet set, EntitlementKey key) =>
        set.TryGet(key, out var entry)
            ? entry
            : new EntitlementEntry(key, key.Default, EntitlementProvenance.CatalogueDefault);

    /// <summary>Merges a lower-precedence contribution into what higher-precedence sources have
    /// already said. The value is the more generous of the two; the credit goes to the incoming
    /// source only when it strictly beat what was there, because sources are visited highest first
    /// and a tie should be attributed to the one with the better standing.</summary>
    private static EntitlementEntry Combine(EntitlementEntry existing, EntitlementEntry incoming)
    {
        var merged = EntitlementValue.Merge(existing.Value, incoming.Value);

        return merged.Raw > existing.Value.Raw
            ? incoming with { Value = merged }
            : existing with { Value = merged };
    }

    /// <summary>The paired rule.</summary>
    private static EntitlementEntry Restrict(EntitlementEntry guild, EntitlementEntry user)
    {
        var restricted = EntitlementValue.Restrict(guild.Value, user.Value);

        return restricted.Raw < guild.Value.Raw
            ? user with { Value = restricted }
            : guild with { Value = restricted };
    }
}
