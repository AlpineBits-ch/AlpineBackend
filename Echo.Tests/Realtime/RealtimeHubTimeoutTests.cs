using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Echo.Tests.Realtime;

/// <summary>Pins the two numbers that decide when the gateway declares a client dead.</summary>
[TestFixture]
[Category("Unit")]
public class RealtimeHubTimeoutTests
{
    /// <summary>The options SignalR itself would resolve, rather than the constants read back - the
    /// registration is the half that can be wrong.</summary>
    private static HubOptions Resolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR(RealtimeHubTimeouts.Configure);
        return services.BuildServiceProvider().GetRequiredService<IOptions<HubOptions>>().Value;
    }

    [Test]
    public void The_client_timeout_outlasts_a_backgrounded_Chromium_wake_up()
    {
        var timeout = Resolved().ClientTimeoutInterval;

        Assert.Multiple(() =>
        {
            Assert.That(timeout, Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(timeout, Is.GreaterThan(TimeSpan.FromSeconds(60)),
                "a throttled page wakes about once a minute; a timeout at or under that evicts "
                + "healthy clients for being backgrounded");
            Assert.That(timeout, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(120)),
                "and the cost of the margin is how long a hard-killed client keeps its place");
        });
    }

    /// <summary>
    /// The keep-alive is judged against the client's timeout, never against ours.
    /// </summary>
    [Test]
    public void The_keep_alive_still_clears_the_timeout_of_a_client_we_are_not_shipping()
    {
        var options = Resolved();

        // What an un-updated SignalR client waits before concluding the server is gone.
        var defaultClientServerTimeout = TimeSpan.FromSeconds(30);

        Assert.Multiple(() =>
        {
            Assert.That(options.KeepAliveInterval, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(options.KeepAliveInterval * 2, Is.LessThanOrEqualTo(defaultClientServerTimeout),
                "a single dropped ping must not be enough for an un-updated client - mobile "
                + "included - to conclude the gateway is gone");
        });
    }
}
