using Wolverine;
using Wolverine.Runtime;

namespace Echo.Sagas;

/// <summary>
/// Keeps every external Wolverine listener switched off for the duration of
/// <c>WolverineRuntime.StartAsync</c>, and switches them on again the moment that has returned.
/// </summary>
public static class WolverineListenerStartup
{
    /// <summary>Call inside <c>UseWolverine</c>.</summary>
    public static WolverineOptions DeferListenerStartup(this WolverineOptions opts)
    {
        opts.DisableAllExternalListeners = true;
        return opts;
    }

    /// <summary>
    /// The two steps that undo <see cref="DeferListenerStartup"/>, split out from the hosted
    /// service so the ordering between them is directly testable.
    /// </summary>
    public static async Task ResumeListenersAsync(WolverineOptions options, Func<Task> startListeners)
    {
        options.DisableAllExternalListeners = false;
        await startListeners();
    }
}

/// <summary>
/// Starts the listeners <see cref="WolverineListenerStartup.DeferListenerStartup"/> held back.
/// </summary>
public sealed class DeferredWolverineListeners(
    IWolverineRuntime runtime,
    ILogger<DeferredWolverineListeners> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Order matters: StartListenersAsync short-circuits on the flag, so it has to be cleared
        // first.
        await WolverineListenerStartup.ResumeListenersAsync(
            runtime.Options, () => runtime.Endpoints.StartListenersAsync());

        logger.LogInformation(
            "Wolverine external listeners started after the runtime finished starting");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
