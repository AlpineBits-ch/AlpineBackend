using Isle.Api.Chat.Commands;

namespace Isle.Api.Chat;

/// <summary>
/// The set of <c>!commands</c> the server understands, plus the name → type lookup used to dispatch
/// them. Registered as a singleton; the lookup is built once by instantiating each command through
/// <see cref="ActivatorUtilities"/> purely to read its <see cref="ChatCommand.Name"/>. The real
/// instance a player's message runs against is created fresh per message from that message's scope,
/// so scoped dependencies (e.g. <c>MicroserviceContext</c>) are never captured for the process
/// lifetime.
/// </summary>
public sealed class ChatCommandRegistry
{
    /// <summary>Add a new command here and it becomes callable in-game. Nothing else to wire up.</summary>
    public static IReadOnlyList<Type> RegisteredTypes { get; } =
    [
        typeof(DebugCommand), typeof(LinkInGameName), typeof(PromoteCommand),
        typeof(StoreDinoCommand), typeof(LoadDinoCommand), typeof(BuySlotCommand), typeof(StorageInfoCommand),
        typeof(SendInviteCommand), typeof(AcceptInviteCommand), typeof(RejectInviteCommand),
        typeof(WhoAmICommand), typeof(WipeWorldCommand), typeof(HelpCommand), typeof(SkinCommand)
    ];

    private readonly Dictionary<string, Type> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ChatCommandRegistry(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        foreach (var type in RegisteredTypes)
        {
            // The throwaway prototype (and any scoped deps it pulled) is disposed with this scope.
            if (ActivatorUtilities.CreateInstance(scope.ServiceProvider, type) is ChatCommand prototype)
                _byName[prototype.Name] = type;
        }
    }

    public bool TryResolve(string commandName, out Type commandType) =>
        _byName.TryGetValue(commandName, out commandType!);
}
