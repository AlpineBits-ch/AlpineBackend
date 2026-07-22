using Isle.Api.Services;

namespace Isle.Api.Chat.CommandController.Commands;

public class WipeWorldCommand(WorldCleaner cleaner) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        await cleaner.WipeAsync();
        return "World cleanup triggered: corpses wiped and AI reset.";
    }

    public override string Name { get; } = "wipeworld";
    public override string Description { get; } = "Wipes all corpses and resets AI now (admin; ignores the population gate)";
    public override bool IsAdminOnly { get; set; } = true;
}
