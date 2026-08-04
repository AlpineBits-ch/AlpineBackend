using Echo.Sagas;
using Wolverine;

namespace Echo.Tests.Sagas;

/// <summary>
/// Guards the gateway's start-up ordering rule: nothing may be consumed while Wolverine's runtime
/// is still starting.
/// </summary>
[TestFixture]
public class WolverineListenerStartupTests
{
    [Test]
    public void DeferListenerStartup_suppresses_listener_startup()
    {
        var options = new WolverineOptions();

        var returned = options.DeferListenerStartup();

        Assert.Multiple(() =>
        {
            Assert.That(options.DisableAllExternalListeners, Is.True);
            Assert.That(returned, Is.SameAs(options), "must chain like the rest of the Wolverine config");
        });
    }

    [Test]
    public async Task ResumeListenersAsync_clears_the_suppression_and_starts_listening()
    {
        var options = new WolverineOptions().DeferListenerStartup();
        var started = 0;

        await WolverineListenerStartup.ResumeListenersAsync(options, () =>
        {
            started++;
            return Task.CompletedTask;
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.DisableAllExternalListeners, Is.False);
            Assert.That(started, Is.EqualTo(1));
        });
    }

    /// <summary>The ordering is the whole guard.</summary>
    [Test]
    public async Task ResumeListenersAsync_clears_the_suppression_before_it_starts_listening()
    {
        var options = new WolverineOptions().DeferListenerStartup();
        bool? suppressedWhenStarted = null;

        await WolverineListenerStartup.ResumeListenersAsync(options, () =>
        {
            suppressedWhenStarted = options.DisableAllExternalListeners;
            return Task.CompletedTask;
        });

        Assert.That(suppressedWhenStarted, Is.False,
            "StartListenersAsync is a no-op while DisableAllExternalListeners is set");
    }

    /// <summary>
    /// Resuming a runtime that was never suppressed must still start listening rather than
    /// assuming the flag tells it whether there is anything to do.
    /// </summary>
    [Test]
    public async Task ResumeListenersAsync_starts_listening_even_when_it_was_never_suppressed()
    {
        var options = new WolverineOptions();
        var started = 0;

        await WolverineListenerStartup.ResumeListenersAsync(options, () =>
        {
            started++;
            return Task.CompletedTask;
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.DisableAllExternalListeners, Is.False);
            Assert.That(started, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A broker that is unreachable when the listeners resume must surface, not be swallowed -
    /// otherwise the gateway comes up healthy and permanently deaf.
    /// </summary>
    [Test]
    public void ResumeListenersAsync_propagates_a_failure_to_start_listening()
    {
        var options = new WolverineOptions().DeferListenerStartup();

        Assert.That(
            async () => await WolverineListenerStartup.ResumeListenersAsync(
                options, () => throw new InvalidOperationException("broker down")),
            Throws.InstanceOf<InvalidOperationException>().With.Message.EqualTo("broker down"));
    }
}
