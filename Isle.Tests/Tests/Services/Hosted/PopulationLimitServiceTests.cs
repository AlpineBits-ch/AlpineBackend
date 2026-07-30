using System.Reflection;
using Isle.Api.Services;
using Isle.Api.Services.Hosted;
using Isle.Api.Services.Rcon;
using IsleBridge.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions.Models;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="PopulationLimitService"/> has no <c>IServiceScopeFactory</c> dependency at all — it only
/// needs <see cref="IRconGateway"/> and <see cref="SpeciesPopulationLimits"/> — so its per-tick logic
/// (<c>EnforceAsync</c>, made `internal` for this) can be driven directly without any DI plumbing.
/// </summary>
[TestFixture]
public class PopulationLimitServiceTests
{
    private IRconGateway _rcon = null!;
    private SpeciesPopulationLimits _limits = null!;
    private PopulationLimitService _service = null!;
    private List<string> _sentPlayables = null!;

    [SetUp]
    public void SetUp()
    {
        _rcon = Substitute.For<IRconGateway>();
        _sentPlayables = [];

        // The UpdatePlayables call is `rcon.ExecuteAsync(client => client.SendCommandAsync(cmd, playables))`
        // — a Func<EvrimaRconClient, Task<string>> that closes over the locally-computed `playables`
        // string. Rather than invoke it against a real (socket-backed) EvrimaRconClient, pull the
        // captured value straight off the compiler-generated closure via reflection.
        _rcon.ExecuteAsync(Arg.Do<Func<EvrimaRconClient, Task<string>>>(CapturePlayables))
            .Returns(Task.FromResult("ok"));

        _limits = new SpeciesPopulationLimits
        {
            Caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [Species.Tyrannosaurus] = 2,
                [Species.Triceratops] = 0, // always disabled, per production config's "temporarely" comment
                [Species.Gallimimus] = SpeciesPopulationLimits.Unlimited,
            },
        };
        _service = new PopulationLimitService(_rcon, _limits, NullLogger<PopulationLimitService>.Instance);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    private void CapturePlayables(Func<EvrimaRconClient, Task<string>> operation)
    {
        var closureFields = operation.Target?.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var value = closureFields?.Select(f => f.GetValue(operation.Target)).OfType<string>().FirstOrDefault();
        if (value is not null)
            _sentPlayables.Add(value);
    }

    private void SetRoster(params PlayerData[] players) =>
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task<List<PlayerData>>>>())
            .Returns(Task.FromResult(players.ToList()));

    private static PlayerData Player(string species) => new() { PlayerId = Guid.NewGuid().ToString(), Class = species, Name = "p" };

    [Test]
    public async Task EnforceAsync_SpeciesUnderCap_SendsItAsPlayable()
    {
        SetRoster(Player(Species.Tyrannosaurus)); // 1 of 2 -> under cap

        await _service.EnforceAsync();

        Assert.That(_sentPlayables.Single().Split(','), Does.Contain(Species.Tyrannosaurus));
    }

    [Test]
    public async Task EnforceAsync_SpeciesAtCap_ExcludesItFromPlayables()
    {
        SetRoster(Player(Species.Tyrannosaurus), Player(Species.Tyrannosaurus)); // 2 of 2 -> at cap

        await _service.EnforceAsync();

        Assert.That(_sentPlayables.Single().Split(','), Does.Not.Contain(Species.Tyrannosaurus));
    }

    [Test]
    public async Task EnforceAsync_UnlimitedSpecies_AlwaysIncludedRegardlessOfCount()
    {
        SetRoster(Enumerable.Range(0, 50).Select(_ => Player(Species.Gallimimus)).ToArray());

        await _service.EnforceAsync();

        Assert.That(_sentPlayables.Single().Split(','), Does.Contain(Species.Gallimimus));
    }

    [Test]
    public async Task EnforceAsync_CapZeroSpecies_NeverIncludedEvenWithNobodyAlive()
    {
        SetRoster(); // empty roster

        await _service.EnforceAsync();

        Assert.That(_sentPlayables.Single().Split(','), Does.Not.Contain(Species.Triceratops));
    }

    [Test]
    public async Task EnforceAsync_UnchangedState_DoesNotResendUpdatePlayablesOnSecondTick()
    {
        SetRoster(Player(Species.Tyrannosaurus));

        await _service.EnforceAsync();
        await _service.EnforceAsync(); // same roster -> same desired state

        Assert.That(_sentPlayables, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EnforceAsync_StateChanges_ResendsUpdatePlayablesOnSecondTick()
    {
        SetRoster(Player(Species.Tyrannosaurus));
        await _service.EnforceAsync();

        SetRoster(Player(Species.Tyrannosaurus), Player(Species.Tyrannosaurus)); // now at cap
        await _service.EnforceAsync();

        Assert.That(_sentPlayables, Has.Count.EqualTo(2));
        Assert.That(_sentPlayables[0], Is.Not.EqualTo(_sentPlayables[1]));
    }

    [Test]
    public void SafeEnforceAsync_RconThrows_SwallowsTheException()
    {
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task<List<PlayerData>>>>())
            .ThrowsAsync(new InvalidOperationException("rcon socket dead"));

        Assert.DoesNotThrowAsync(() => _service.SafeEnforceAsync());
    }

    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_StillRunsTheBootTimeEnforcementOnce()
    {
        // ExecuteAsync calls SafeEnforceAsync unconditionally before entering the while(ct) loop, so
        // even a token that's cancelled essentially immediately should still see one enforcement pass.
        SetRoster(Player(Species.Tyrannosaurus));
        using var cts = new CancellationTokenSource();

        await _service.StartAsync(cts.Token);
        await Task.Delay(50); // give the boot-time pass a chance to actually run before we tear it down
        cts.Cancel();
        await _service.StopAsync(CancellationToken.None);

        Assert.That(_sentPlayables, Has.Count.EqualTo(1));
    }
}
