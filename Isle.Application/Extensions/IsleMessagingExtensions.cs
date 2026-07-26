using Isle.Contracts.Events.Player;
using Wolverine;

namespace Isle.Api.Extensions;

public static class IsleMessagingExtensions
{
    /// <summary>Local queue the damage feed lands on. Named so it is identifiable in Wolverine diagnostics.</summary>
    private const string DamageQueue = "isle-damage";

    /// <summary>
    /// Isle-specific routing applied on top of the shared Wolverine convention. Must be called
    /// <i>after</i> <c>ConfigureWolverine()</c>, since it overrides what that sets up.
    ///
    /// <para><see cref="PlayerDamagedEvent"/> is the one message on this service that arrives at fight
    /// cadence rather than at event cadence — several per second per ongoing fight, against a handful
    /// per minute for joins, deaths and kills. The shared policy makes every message durable: written
    /// to Postgres on the way out, pushed through RabbitMQ, written again on the way in. That is the
    /// right trade for a kill, which must never be lost, and the wrong one for damage, which would put
    /// the database under a write load proportional to how busy the server is — exactly backwards.</para>
    ///
    /// <para>So damage gets a buffered local queue instead: in memory, no broker hop, no durability,
    /// dropped on shutdown. That is honest about what it is for. The only subscriber is bounty
    /// participation, whose ledger is already a best-effort Redis write, and losing a swing costs one
    /// player a little credit on one bounty. Anything that ever needs damage durably should take it
    /// from the bridge feed directly rather than making every fight pay for it.</para>
    /// </summary>
    public static WolverineOptions ConfigureIsleMessaging(this WolverineOptions opts)
    {
        opts.PublishMessage<PlayerDamagedEvent>().ToLocalQueue(DamageQueue);
        opts.LocalQueue(DamageQueue).BufferedInMemory();

        return opts;
    }
}
