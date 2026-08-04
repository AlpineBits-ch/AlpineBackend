using Wolverine;
using Wolverine.Runtime;

namespace Echo.Sagas;

/// <summary>
/// Keeps every external Wolverine listener switched off for the duration of
/// <c>WolverineRuntime.StartAsync</c>, and switches them on again the moment that has returned.
///
/// <para><b>Why this exists.</b> Wolverine's own start-up sequence is
/// <c>HandlerGraph.Compile</c> → declare exchanges/queues → <c>StartListenersAsync</c> → start the
/// node agents → <c>PrepopulateRoutingCache(Handlers.AllMessageTypes())</c>. That last call
/// enumerates a <i>lazy</i> iterator: <c>AllMessageTypes()</c> yields, per handler chain, the
/// chain's own message type and then everything the chain publishes, and the published-type half
/// walks the chain's live <c>Handlers</c> list (<c>HandlerChain.PublishedTypes</c> is
/// <c>Handlers.SelectMany(x =&gt; x.Creates)</c>). Resolving a route for each yielded type talks to
/// the broker, so the enumerator sits suspended - mid-list - for as long as those round trips
/// take.</para>
///
/// <para>Meanwhile the listeners started two steps earlier are already pulling messages. Under
/// dynamic code generation - which is what every non-container run of this gateway uses, because
/// <c>dotnet Echo.dll</c> over raw build output never ran the Dockerfile's
/// <c>codegen write</c> step - the first message of a given type compiles its handler chain on the
/// listener's thread. For a saga chain that compilation calls
/// <c>Wolverine.Persistence.Sagas.SagaChain.DetermineFrames</c>, whose second act is
/// <c>Handlers.Clear()</c>. Clearing the very <c>List&lt;MethodCall&gt;</c> the start-up thread is
/// suspended inside throws <c>InvalidOperationException: Collection was modified</c> out of
/// <c>StartAsync</c>, and the whole host exits before it ever becomes healthy.</para>
///
/// <para>The trigger is not exotic: it is a durable <c>Echo.Sagas.*</c> queue with anything in it
/// when the gateway boots - a redelivery, a backlog from a previous instance, or in CI the
/// acknowledgement another test fixture's stack left behind on the shared broker. Nothing in this
/// repository can make that window safe from the inside, because both mutating and enumerating
/// happen in Wolverine. What we <i>can</i> do is make sure no message is being handled while the
/// window is open, which is exactly what deferring listener start-up buys.</para>
///
/// <para><b>Why this shape.</b> <see cref="WolverineOptions.DisableAllExternalListeners"/> is read
/// in exactly one place in Wolverine - <c>EndpointCollection.StartListenersAsync</c> - so setting
/// it changes when listeners start and nothing else. Every earlier step still runs untouched:
/// conventional routing still discovers listener endpoints, queues and bindings are still declared
/// and provisioned, senders are still built. Pre-compiling the saga chains instead (the obvious
/// alternative) is actively unsafe here: compiling a saga chain empties its <c>Handlers</c> list,
/// and Wolverine's conventional listener discovery decides whether to create the
/// <c>Echo.Sagas.*</c> queues by asking <c>chain.Handlers.Any()</c> - so a pre-compiled gateway
/// would come up with no saga queues at all and no error to say so.</para>
///
/// <para>The cost is that the gateway's HTTP surface is reachable for the few milliseconds between
/// the host starting and this service running. Messages published in that gap are not lost: they
/// sit in their durable queues and are picked up as soon as the listeners come up.</para>
/// </summary>
public static class WolverineListenerStartup
{
    /// <summary>
    /// Call inside <c>UseWolverine</c>. Suppresses listener start-up during
    /// <c>WolverineRuntime.StartAsync</c>; <see cref="DeferredWolverineListeners"/> undoes it.
    /// </summary>
    public static WolverineOptions DeferListenerStartup(this WolverineOptions opts)
    {
        opts.DisableAllExternalListeners = true;
        return opts;
    }

    /// <summary>
    /// The two steps that undo <see cref="DeferListenerStartup"/>, split out from the hosted
    /// service so the ordering between them is directly testable. It is load-bearing:
    /// <c>StartListenersAsync</c> returns immediately while
    /// <see cref="WolverineOptions.DisableAllExternalListeners"/> is still set, so doing these the
    /// other way round would leave the gateway healthy, silent, and consuming nothing.
    /// </summary>
    public static async Task ResumeListenersAsync(WolverineOptions options, Func<Task> startListeners)
    {
        options.DisableAllExternalListeners = false;
        await startListeners();
    }
}

/// <summary>
/// Starts the listeners <see cref="WolverineListenerStartup.DeferListenerStartup"/> held back.
/// Must be registered <b>after</b> <c>UseWolverine</c> so the host runs it after Wolverine's own
/// runtime has finished starting - see <see cref="WolverineListenerStartup"/> for why that
/// ordering is the entire point.
/// </summary>
public sealed class DeferredWolverineListeners(
    IWolverineRuntime runtime,
    ILogger<DeferredWolverineListeners> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Order matters: StartListenersAsync short-circuits on the flag, so it has to be cleared
        // first. Clearing it also leaves the runtime in the state the rest of Wolverine expects,
        // so anything that starts a listener later (an endpoint circuit recovering, say) behaves
        // normally.
        await WolverineListenerStartup.ResumeListenersAsync(
            runtime.Options, () => runtime.Endpoints.StartListenersAsync());

        logger.LogInformation(
            "Wolverine external listeners started after the runtime finished starting");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
