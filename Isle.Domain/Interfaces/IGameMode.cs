using Isle.Domain.Aggregates;
using Isle.Domain.ValueObjects;

namespace Isle.Domain.Interfaces;

public interface IGameMode
{
    Task OnStartAsync(GameModeInstance instance);
    Task OnTickAsync(GameModeInstance instance, TimeSpan elapsed);
    Task OnEndAsync(GameModeInstance instance);

    IReadOnlyList<ParticipantStanding> GetStandings(GameModeInstance instance);

    /// <summary>
    /// Bonus reward rows for this standing, on top of <c>Definition.Rewards</c> - e.g. a mode that pays
    /// extra for never losing the lead. Most modes have nothing to add here and return an empty list.
    /// </summary>
    IReadOnlyList<RewardConfig> GetRewards(GameModeInstance instance, ParticipantStanding standing);
}