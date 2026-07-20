namespace Isle.Api.Chat.CommandController.Commands;

public class DebugCommand : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        return $"Debug command received for {context.PlayerName}";
    }

    public override string Name { get; } = "debug";
    public override string Description { get; } = "Debug command, to see if the game actually deals with commands";
}