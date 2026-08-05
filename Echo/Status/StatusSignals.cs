namespace Echo.Status;

/// <summary>What the detector currently sees, per component, on this replica.</summary>
public sealed record ComponentSignal(
    string Key,
    string Name,
    string Status,
    int Requests,
    int Errors,
    double ErrorRate,
    int Destinations,
    int UnhealthyDestinations,
    string Verdict,
    int BadStreak,
    int CleanStreak,
    bool Suppressed,
    bool Monitored,
    string? OpenIncidentReference);

public sealed record StatusSignalReport(
    DateTimeOffset UpdatedAt,
    bool AutoDetectionEnabled,
    int WindowSeconds,
    int MinimumVolume,
    double DegradedRate,
    double OutageRate,
    double RecoveryRate,
    int OpenSamples,
    int RecoverySamples,
    IReadOnlyList<ComponentSignal> Components);

/// <summary>Holds the latest report for the admin endpoint to read.</summary>
public sealed class StatusSignalBoard
{
    private volatile StatusSignalReport _current =
        new(DateTimeOffset.UnixEpoch, false, 0, 0, 0, 0, 0, 0, 0, []);

    public StatusSignalReport Current => _current;

    public void Set(StatusSignalReport report) => _current = report;
}
