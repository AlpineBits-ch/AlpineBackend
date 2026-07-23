using Isle.Domain.Aggregates;
using Isle.Domain.Interfaces;

namespace Isle.Api.Games.KingOfTheHill;

public class KingOfTheHillMode : IGameMode
{
    public Task OnStartAsync(GameModeInstance instance)
    {
        throw new NotImplementedException();
    }

    public Task OnTickAsync(GameModeInstance instance, TimeSpan elapsed)
    {
        throw new NotImplementedException();
    }

    public Task OnEndAsync(GameModeInstance instance)
    {
        throw new NotImplementedException();
    }

    public IReadOnlyList<ParticipantStanding> GetStandings(GameModeInstance instance)
    {
        throw new NotImplementedException();
    }

    public IReadOnlyList<IReward> GetRewards(GameModeInstance instance, ParticipantStanding standing)
    {
        throw new NotImplementedException();
    }
}