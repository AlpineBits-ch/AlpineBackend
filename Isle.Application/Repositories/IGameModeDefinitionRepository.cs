using Isle.Domain.Aggregates;
using Isle.Domain.Entity;

namespace Isle.Api.Repositories;

public interface IGameModeDefinitionRepository
{
    Task<GameModeDefinition?> GetByIdAsync(Guid id);
    Task<List<GameModeDefinition>> GetEnabledAsync();
    Task SaveRunResultAsync(GameModeRun run);
}