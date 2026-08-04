using System.Diagnostics.Metrics;

namespace Echo.Tests.Support;

/// <summary>One recorded counter increment, with its tags flattened to strings.</summary>
public sealed record CounterMeasurement(long Value, IReadOnlyDictionary<string, string?> Tags);

/// <summary>
/// Captures a single counter off a <see cref="Meter"/> for the duration of a test.
///
/// <para>The deadline alert's metric is the part an operator writes an alert rule against, so
/// "an Error was logged" is not enough to prove the alarm works - the counter has to fire, once per
/// missing service, tagged with which one.</para>
/// </summary>
public sealed class CounterRecorder : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<CounterMeasurement> _measurements = new();
    private readonly object _gate = new();

    public CounterRecorder(string meterName, string instrumentName)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            var flattened = new Dictionary<string, string?>();
            foreach (var tag in tags)
            {
                flattened[tag.Key] = tag.Value?.ToString();
            }

            lock (_gate)
            {
                _measurements.Add(new CounterMeasurement(measurement, flattened));
            }
        });

        _listener.Start();
    }

    public IReadOnlyList<CounterMeasurement> Measurements
    {
        get
        {
            lock (_gate) return _measurements.ToList();
        }
    }

    public IReadOnlyList<string?> TagValues(string tag) =>
        Measurements.Select(m => m.Tags.GetValueOrDefault(tag)).ToList();

    public void Dispose() => _listener.Dispose();
}
