using Microsoft.Extensions.DependencyInjection;

namespace Isle.Api.Chat.CommandController.Commands;

public class HelpCommand(IServiceProvider serviceProvider) : ChatCommand
{
    public override Task<string> ExecuteAsync(CommandContext context)
    {
        // Instantiate every registered command just to read its metadata (Name/Description/etc.),
        // mirroring how the controller builds its lookup table. Constructors only capture deps.
        var commands = new List<ChatCommand>();
        foreach (var type in CommandController.RegisteredTypes)
        {
            if (ActivatorUtilities.CreateInstance(serviceProvider, type) is ChatCommand cmd)
                commands.Add(cmd);
        }

        // Only surface commands this player is allowed to run (hides admin-only from normal players).
        var visible = commands.Where(c => c.CanRun(context)).ToList();

        var requested = (context.Arguments.FirstOrDefault() ?? string.Empty).Trim().TrimStart('!');

        if (requested.Length == 0)
        {
            var names = string.Join(", ", visible.Select(c => c.Name).OrderBy(n => n));
            return Task.FromResult($"Commands: {names}. Type !help name for details on one.");
        }

        var match = visible.FirstOrDefault(c => string.Equals(c.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Task.FromResult($"No command named {requested}. Type !help to list commands.");
        }

        // Keep this plain ASCII: the in-game font renders unusual glyphs as raw unicode escapes.
        var details = $"!{match.Name}: {match.Description}";
        if (match.Cooldown > TimeSpan.Zero)
            details += $" (cooldown {Math.Ceiling(match.Cooldown.TotalSeconds)}s)";
        if (match.IsAdminOnly)
            details += " (admin only)";

        return Task.FromResult(details);
    }

    public override string Name { get; } = "help";
    public override string Description { get; } = "Lists commands, or shows details for one. Usage: !help or !help name";
    public override bool IsAdminOnly { get; set; } = false;
}
