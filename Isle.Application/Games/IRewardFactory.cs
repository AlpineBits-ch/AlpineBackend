using Isle.Domain.Interfaces;
using Isle.Domain.ValueObjects;

namespace Isle.Api.Games;

public interface IRewardFactory
{
    IReward Create(RewardConfig config);
}