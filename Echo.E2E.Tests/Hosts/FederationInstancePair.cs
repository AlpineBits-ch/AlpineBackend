using Echo.E2E.Tests.Fixtures;

namespace Echo.E2E.Tests.Hosts;

/// <summary>
/// Boots two fully independent <see cref="EchoTestStack"/>s - each with its own
/// <see cref="EchoInfraSet"/> (own Postgres/RabbitMQ/Redis/Scylla, own Ed25519 federation
/// keypair) - to simulate two real, separately-deployed Echo instances federating with each
/// other. Nothing is shared between them: that's the point. Two real federated Echo deployments
/// never share infra, and split-brain scenarios (Phase 4/6) need to be able to take one instance
/// down without touching the other.
/// </summary>
public sealed class FederationInstancePair : IAsyncDisposable
{
    public EchoInfraSet InfraA { get; }
    public EchoInfraSet InfraB { get; }
    public EchoTestStack A { get; }
    public EchoTestStack B { get; }

    private FederationInstancePair(EchoInfraSet infraA, EchoInfraSet infraB, EchoTestStack a, EchoTestStack b)
    {
        InfraA = infraA;
        InfraB = infraB;
        A = a;
        B = b;
    }

    public static async Task<FederationInstancePair> StartAsync(
        string instanceNameA = "instance-a", string instanceNameB = "instance-b")
    {
        // Every real resource acquired along the way is tracked here as soon as it exists, and
        // cleaned up from a single place on any failure - a previous version disposed infra but
        // not an already-started EchoTestStack (up to 7 real OS processes each) if a *later*
        // step failed, leaking them for the rest of the run and locking their build output.
        EchoInfraSet? infraA = null;
        EchoInfraSet? infraB = null;
        EchoTestStack? stackA = null;
        EchoTestStack? stackB = null;

        try
        {
            infraA = await EchoInfraSet.StartAsync();
            infraB = await EchoInfraSet.StartAsync();

            await Task.WhenAll(
                infraA.CreateDatabasesAsync(EchoInfraFixture.DatabaseNames),
                infraB.CreateDatabasesAsync(EchoInfraFixture.DatabaseNames));

            // Sequential, not parallel: a failure here should point at exactly which instance's
            // stack didn't come up, with that instance's own captured output.
            stackA = await EchoTestStack.StartAsync(infraA, "a", instanceNameA);
            stackB = await EchoTestStack.StartAsync(infraB, "b", instanceNameB);

            return new FederationInstancePair(infraA, infraB, stackA, stackB);
        }
        catch
        {
            var cleanup = new List<Task>();
            if (stackB is not null) cleanup.Add(stackB.DisposeAsync().AsTask());
            if (stackA is not null) cleanup.Add(stackA.DisposeAsync().AsTask());
            if (infraB is not null) cleanup.Add(infraB.DisposeAsync().AsTask());
            if (infraA is not null) cleanup.Add(infraA.DisposeAsync().AsTask());
            await Task.WhenAll(cleanup);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(
            A.DisposeAsync().AsTask(),
            B.DisposeAsync().AsTask());
        await Task.WhenAll(
            InfraA.DisposeAsync().AsTask(),
            InfraB.DisposeAsync().AsTask());
    }
}
