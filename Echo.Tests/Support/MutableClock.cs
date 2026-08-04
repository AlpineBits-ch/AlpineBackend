namespace Echo.Tests.Support;

/// <summary>A <see cref="TimeProvider"/> a test moves by hand. Enough for the two windows the
/// telemetry consent mirror cares about, and cheaper than taking a dependency on a test-time
/// package for one method.</summary>
public sealed class MutableClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
