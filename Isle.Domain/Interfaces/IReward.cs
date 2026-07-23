namespace Isle.Domain.Interfaces;

public interface IReward
{
    Task GrantAsync(Guid playerId);
}
