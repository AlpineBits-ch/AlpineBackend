using Echo.E2E.Tests.Fixtures;

namespace Echo.E2E.Tests.Hosts;

/// <summary>
/// Boots two fully independent <see cref="EchoTestStack"/>s - each with its own <see
/// cref="EchoInfraSet"/> (own Postgres/RabbitMQ/Redis/Scylla, own Ed25519 federation keypair) - to
/// simulate two real, separately-deployed Echo instances federating with each other.
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
        var infraA = await EchoInfraSet.StartAsync();
        var infraB = await EchoInfraSet.StartAsync();

        try
        {
            await Task.WhenAll(
                infraA.CreateDatabasesAsync(EchoInfraFixture.DatabaseNames),
                infraB.CreateDatabasesAsync(EchoInfraFixture.DatabaseNames));

            EchoTestStack stackA, stackB;
            try
            {
                // Sequential, not parallel: a failure here should point at exactly which
                // instance's stack didn't come up, with that instance's own captured output.
                stackA = await EchoTestStack.StartAsync(infraA, "a", instanceNameA);
                stackB = await EchoTestStack.StartAsync(infraB, "b", instanceNameB);
            }
            catch
            {
                await Task.WhenAll(infraA.DisposeAsync().AsTask(), infraB.DisposeAsync().AsTask());
                throw;
            }

            return new FederationInstancePair(infraA, infraB, stackA, stackB);
        }
        catch
        {
            await Task.WhenAll(infraA.DisposeAsync().AsTask(), infraB.DisposeAsync().AsTask());
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
