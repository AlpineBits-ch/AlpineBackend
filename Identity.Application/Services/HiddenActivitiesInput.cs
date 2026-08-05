using Identity.Application.Dtos.Request;

namespace Identity.Application.Services;

/// <summary>The outcome of validating a suppressed-games payload.</summary>
public sealed record HiddenActivitiesParseResult
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }

    public IReadOnlySet<string> ApplicationIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> Names { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static HiddenActivitiesParseResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>Validates and normalizes the suppressed-games list.</summary>
public static class HiddenActivitiesInput
{
    public static HiddenActivitiesParseResult Parse(IReadOnlyList<HiddenActivityDto>? entries)
    {
        var incoming = entries ?? [];

        if (incoming.Count > HiddenActivityLimits.MaxEntries)
            return HiddenActivitiesParseResult.Fail(
                $"At most {HiddenActivityLimits.MaxEntries} hidden activities are allowed.");

        var applicationIds = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in incoming)
        {
            if (entry is null) return HiddenActivitiesParseResult.Fail("Entries must not be null.");

            var applicationId = Trimmed(entry.ApplicationId);
            var name = Trimmed(entry.Name);

            // Exactly one key.
            if ((applicationId is null) == (name is null))
                return HiddenActivitiesParseResult.Fail("Each entry must set exactly one of applicationId or name.");

            if (applicationId is not null)
            {
                if (applicationId.Length > HiddenActivityLimits.MaxApplicationIdLength
                    || !applicationId.All(char.IsAsciiDigit))
                {
                    return HiddenActivitiesParseResult.Fail($"'{applicationId}' is not a valid application id.");
                }

                applicationIds.Add(applicationId);
            }
            else
            {
                if (name!.Length > HiddenActivityLimits.MaxNameLength)
                {
                    return HiddenActivitiesParseResult.Fail(
                        $"Names must be {HiddenActivityLimits.MaxNameLength} characters or fewer.");
                }

                names.Add(name);
            }
        }

        return new HiddenActivitiesParseResult { Ok = true, ApplicationIds = applicationIds, Names = names };
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
