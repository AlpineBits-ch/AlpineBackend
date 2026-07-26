using Isle.Contracts.Events.Player;
using Wolverine;

namespace Isle.Api.Extensions;

public static class IsleMessagingExtensions
{
    /// <summary>Local queue the damage feed lands on. Named so it is identifiable in Wolverine diagnostics.</summary>
    private const string DamageQueue = "isle-damage";

    /// <summary>Isle-specific routing applied on top of the shared Wolverine convention.</summary>
    public static WolverineOptions ConfigureIsleMessaging(this WolverineOptions opts)
    {
        opts.PublishMessage<PlayerDamagedEvent>().ToLocalQueue(DamageQueue);
        opts.LocalQueue(DamageQueue).BufferedInMemory();

        return opts;
    }
}
