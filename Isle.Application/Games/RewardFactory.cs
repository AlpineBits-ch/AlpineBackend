using Isle.Domain.Interfaces;
using Isle.Domain.ValueObjects;

namespace Isle.Api.Games;

public class RewardFactory : IRewardFactory
{
    
    public IReward Create(RewardConfig config)
    {
        return config.RewardType switch
        {
            _ => throw new InvalidOperationException($"Unknown reward type '{config.RewardType}'")
        };
    }
}