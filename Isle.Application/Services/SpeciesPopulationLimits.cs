using IsleBridge.Sdk;

namespace Isle.Api.Services;

/// <summary>
/// Configurable per-species population caps enforced by <see cref="PopulationLimitService"/>.
/// </summary>
public sealed class SpeciesPopulationLimits
{
    /// <summary>Sentinel cap meaning "no limit — always playable".</summary>
    public const int Unlimited = -1;

    /// <summary>Species short name (see <see cref="Species"/>) → max concurrent alive.</summary>
    public IDictionary<string, int> Caps { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // everything smaller is uncapped (Unlimited).

            // --- Carnivores ---
            [Species.Tyrannosaurus] = 15,
            [Species.Deinosuchus] = 15,
            [Species.Allosaurus] = 20,
            [Species.Carnotaurus] = 25,
            [Species.Ceratosaurus] = 30,
            [Species.Dilophosaurus] = Unlimited,
            [Species.Herrerasaurus] = Unlimited,
            [Species.Omniraptor] = Unlimited,
            [Species.Troodon] = Unlimited,
            [Species.Pteranodon] = Unlimited,
            [Species.Baryonyx] = Unlimited,
            [Species.Austroraptor] = Unlimited,

            // --- Herbivores ---
            [Species.Triceratops] = 25,
            [Species.Stegosaurus] = 25,
            [Species.Diabloceratops] = 30,
            [Species.Kentrosaurus] = Unlimited,
            [Species.Maiasaura] = Unlimited,
            [Species.Pachycephalosaurus] = Unlimited,
            [Species.Tenontosaurus] = Unlimited,
            [Species.Dryosaurus] = Unlimited,
            [Species.Hypsilophodon] = Unlimited,

            // --- Omnivores ---
            [Species.Gallimimus] = Unlimited,
            [Species.Beipiaosaurus] = Unlimited,
        };
}
