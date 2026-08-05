using System.Globalization;
using System.Text;
using Social.Contracts.Dtos;
using Social.Domain.Enums;

namespace Social.Api.Services;

/// <summary>
/// Turns an untrusted activity payload into one that is safe to broadcast, or drops it.
/// </summary>
public sealed class ActivityWriteGuard(GameCatalogLookup catalog, ApplicationRegistryResolver registry)
{
    /// <summary>Sources allowed to carry a free-text name with no application id behind it.</summary>
    private static readonly HashSet<ActivitySource> FreeTextSources = [ActivitySource.Manual, ActivitySource.Media];

    /// <summary>Sanitizes and filters <paramref name="input"/>.</summary>
    public async Task<IReadOnlyList<ActivityDto>> SanitizeAsync(
        IReadOnlyList<ActivityDto>? input,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (input is null || input.Count == 0) return [];

        var results = new List<ActivityDto>(ActivityLimits.MaxActivities);

        // Truncate rather than reject the whole payload: a client sending four is a client bug, and
        // failing the write would clear presence entirely over something recoverable.
        foreach (var candidate in input.Take(ActivityLimits.MaxActivities))
        {
            var sanitized = await SanitizeOneAsync(candidate, now, ct);
            if (sanitized is not null) results.Add(sanitized);
        }

        return results;
    }

    private async Task<ActivityDto?> SanitizeOneAsync(ActivityDto? candidate, DateTimeOffset now, CancellationToken ct)
    {
        if (candidate is null) return null;

        // Enum member names, not ordinals: Enum.TryParse would otherwise accept "3" and silently
        // produce a type nobody sent. Same reasoning as PresenceProjection.TryParse.
        if (!TryParseName<ActivityType>(candidate.Type, out var type)) return null;
        if (!TryParseName<ActivitySource>(candidate.Source, out var source)) return null;

        // Rejected when over-length, not truncated the way the text fields are.
        var applicationId = Clean(candidate.ApplicationId, ActivityLimits.MaxApplicationIdLength + 1);
        if (applicationId is { Length: > ActivityLimits.MaxApplicationIdLength }) return null;

        string? name;

        if (!string.IsNullOrEmpty(applicationId))
        {
            // The whole control. A resolved id means the catalog names the game, not the caller.
            name = await catalog.ResolveCanonicalNameAsync(applicationId, ct);

            // Missing from the catalog is not the same as unknown.
            name ??= await registry.ResolveAndStoreAsync(applicationId, ct);

            if (name is null) return null;
        }
        else
        {
            if (!FreeTextSources.Contains(source)) return null;

            name = Clean(candidate.Name, ActivityLimits.MaxNameLength);
            if (string.IsNullOrEmpty(name)) return null;
        }

        return new ActivityDto
        {
            Type = type.ToString(),
            Name = name,
            Details = Clean(candidate.Details, ActivityLimits.MaxTextLength),
            State = Clean(candidate.State, ActivityLimits.MaxTextLength),
            ApplicationId = string.IsNullOrEmpty(applicationId) ? null : applicationId,
            StartedAt = NormalizeStart(candidate.StartedAt, now),
            EndsAt = NormalizeEnd(candidate.StartedAt, candidate.EndsAt),

            // Artwork is not sourced yet, and until it is, an asset URL on this object would be an
            // arbitrary attacker-chosen URL that every viewer's client would fetch.
            Assets = null,

            Party = SanitizeParty(candidate.Party),
            Source = source.ToString(),
        };
    }

    /// <summary>Range-checks a client-supplied start time.</summary>
    private static long? NormalizeStart(long? startedAt, DateTimeOffset now)
    {
        if (startedAt is not { } value) return null;

        // Dropped rather than clamped.
        if (value <= 0) return null;

        var nowMs = now.ToUnixTimeMilliseconds();

        // A future start renders as a negative or absurd elapsed time on every client.
        if (value > nowMs) return null;

        if (nowMs - value > (long)ActivityLimits.MaxStartAge.TotalMilliseconds) return null;

        return value;
    }

    private static long? NormalizeEnd(long? startedAt, long? endsAt)
    {
        if (endsAt is not { } end || end <= 0) return null;
        if (startedAt is { } start && end <= start) return null;

        return end;
    }

    private static ActivityPartyDto? SanitizeParty(ActivityPartyDto? party)
    {
        if (party is null) return null;

        var size = Clamp(party.Size);
        var max = Clamp(party.Max);

        // "4 of 2" is not a party, it is a rendering bug waiting to happen.
        if (size is not null && max is not null && size > max) size = max;

        var id = Clean(party.Id, ActivityLimits.MaxTextLength);

        if (id is null && size is null && max is null) return null;

        return new ActivityPartyDto { Id = id, Size = size, Max = max };

        static int? Clamp(int? value) => value switch
        {
            null => null,
            < 0 => null,
            > ActivityLimits.MaxPartySize => ActivityLimits.MaxPartySize,
            _ => value,
        };
    }

    /// <summary>Strips what must never reach another user's renderer, then caps length.</summary>
    internal static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);

            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                // Newlines and tabs collapse to a single space rather than vanishing, so
                // "line one\nline two" does not become "line oneline two".
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (ch == ' ')
            {
                if (lastWasSpace || builder.Length == 0) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            builder.Append(ch);
            if (builder.Length >= maxLength) break;
        }

        var cleaned = builder.ToString().TrimEnd();

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Parses an enum by member name only.</summary>
    private static bool TryParseName<T>(string? raw, out T value) where T : struct, Enum
    {
        value = default;

        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Enum.TryParse(raw.Trim(), ignoreCase: true, out T parsed)) return false;
        if (!string.Equals(Enum.GetName(parsed), raw.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        value = parsed;
        return true;
    }
}
