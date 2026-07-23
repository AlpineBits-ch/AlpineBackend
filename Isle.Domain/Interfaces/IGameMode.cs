using Isle.Domain.Aggregates;

namespace Isle.Domain.Interfaces;

public interface IGameMode
{
    Task OnStartAsync(GameModeInstance instance);
    Task OnTickAsync(GameModeInstance instance, TimeSpan elapsed);
    Task OnEndAsync(GameModeInstance instance);

    IReadOnlyList<ParticipantStanding> GetStandings(GameModeInstance instance);
    IReadOnlyList<IReward> GetRewards(GameModeInstance instance, ParticipantStanding standing);
}