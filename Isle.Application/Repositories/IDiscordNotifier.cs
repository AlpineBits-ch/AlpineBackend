using Isle.Domain.Aggregates;
using Isle.Domain.Entity;

namespace Isle.Api.Repositories;

public interface IDiscordNotifier
{
    Task NotifyEventStartedAsync(GameModeInstance instance);
    Task NotifyEventEndedAsync(GameModeRun result);
}