using Echo.Sagas;
using Wolverine;

namespace Echo.Tests.Sagas;

/// <summary>
/// Guards the gateway's start-up ordering rule: nothing may be consumed while Wolverine's runtime
/// is still starting.
///
/// <para>The bug this protects against is not a saga bug - it is that compiling a saga handler
/// chain on a listener thread clears the same <c>List&lt;MethodCall&gt;</c> Wolverine's own
/// <c>PrepopulateRoutingCache</c> is enumerating, which kills the host before it is ever healthy.
/// See <see cref="WolverineListenerStartup"/> for the mechanism. These tests cover the two ways
/// the guard can be silently undone - forgetting to arm it, and resuming in the wrong order - both
/// of which fail quietly in production rather than loudly.</para>
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

    /// <summary>
    /// The ordering is the whole guard. Wolverine's <c>StartListenersAsync</c> returns immediately
    /// while <c>DisableAllExternalListeners</c> is set, so starting listeners before clearing the
    /// flag would leave the gateway up, healthy and consuming nothing at all - a failure with no
    /// exception and no log line. Asserting on what the starter *observes* is what makes swapping
    /// the two lines a red test rather than a silent outage.
    /// </summary>
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
