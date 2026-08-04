using AppEnvironment;
using Echo.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Tests.Privacy;

/// <summary>
/// One pass of the loop the three consuming services run, exercised directly rather than by
/// starting a host and waiting out an interval.
/// </summary>
[TestFixture]
public class TelemetryConsentRefreshTests
{
    private static IServiceScopeFactory ScopeFactory() =>
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static TelemetryConsentRefreshService Service(
        TelemetryConsentSnapshot snapshot, TelemetryConsentResolver resolver) =>
        new(ScopeFactory(), snapshot, resolver, NullLogger<TelemetryConsentRefreshService>.Instance);

    [Test]
    public async Task A_pass_resolves_every_account_telemetry_has_asked_about()
    {
        var snapshot = new TelemetryConsentSnapshot(new MutableClock(), TimeSpan.FromMinutes(1));
        snapshot.Has("yes_user");
        snapshot.Has("no_user");

        IReadOnlyList<string> asked = [];
        var service = Service(snapshot, (_, ids, _) =>
        {
            asked = ids;
            return Task.FromResult<IReadOnlyDictionary<string, bool>>(
                new Dictionary<string, bool> { ["yes_user"] = true, ["no_user"] = false });
        });

        await service.RefreshAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(asked, Is.EquivalentTo(new[] { "yes_user", "no_user" }));
            Assert.That(snapshot.Has("yes_user"), Is.True);
            Assert.That(snapshot.Has("no_user"), Is.False);
        });
    }

    [Test]
    public async Task An_account_the_resolver_could_not_answer_for_stays_unconsenting()
    {
        var snapshot = new TelemetryConsentSnapshot(new MutableClock(), TimeSpan.FromMinutes(1));
        snapshot.Has("unanswerable_user");

        var service = Service(snapshot, (_, _, _) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>()));

        await service.RefreshAsync(CancellationToken.None);

        Assert.That(snapshot.Has("unanswerable_user"), Is.False);
    }

    [Test]
    public void A_resolver_that_throws_does_not_take_a_previous_answer_with_it()
    {
        var snapshot = new TelemetryConsentSnapshot(new MutableClock(), TimeSpan.FromMinutes(1));
        snapshot.Set("yes_user", true);

        var service = Service(snapshot, (_, _, _) =>
            Task.FromException<IReadOnlyDictionary<string, bool>>(new InvalidOperationException("redis is down")));

        // The pass itself surfaces the failure; the loop above it logs and carries on.
        Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync(CancellationToken.None));
        Assert.That(snapshot.Has("yes_user"), Is.True);
    }

    [Test]
    public async Task A_pass_with_nothing_tracked_does_not_call_the_resolver()
    {
        var snapshot = new TelemetryConsentSnapshot(new MutableClock(), TimeSpan.FromMinutes(1));
        var called = false;

        var service = Service(snapshot, (_, _, _) =>
        {
            called = true;
            return Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
        });

        await service.RefreshAsync(CancellationToken.None);

        Assert.That(called, Is.False);
    }
}
