# Chat Command System

A lightweight, extensible chat-command framework for handling in-game `!commands`
sent through the RCON/chat bridge. Commands are picked up as they stream in,
matched by name, executed, and the result is DM'd back to the player.

## Overview

| Component | Location | Purpose |
|---|---|---|
| `ChatStreamIngestionService` | `Services/Ingestion` | The only consumer of the bridge chat feed. Republishes every line as a `ChatMessageReceivedEvent`. |
| `ChatCommandHandler` | `Handlers/Chat` | Wolverine handler for `ChatMessageReceivedEvent`. Routes to the right command and replies via the bridge client. |
| `ChatCommandRegistry` | `Chat` | The list of registered command types and the name → type lookup. |
| `ChatCommand` | `Chat` | Abstract base class that every command implements. |
| `CommandContext` | `Chat` | The data passed into a command when it executes (player info, arguments, live dino stats). |

The ingestion service and the handler are deliberately separate: `IChatStream.StreamAsync`
opens a **new SSE connection per call**, so anything that reads the feed directly is
another socket against the game server. Subscribing to the event instead means chat
reactions can be added freely without multiplying connections.

## How it works

1. `ChatStreamIngestionService` subscribes to `chat.StreamAsync(ct)` and publishes a
   `ChatMessageReceivedEvent` for each line. It reconnects on its own if the feed drops.
2. `ChatCommandHandler` receives the event. Any message that doesn't start with `!` is ignored.
3. The first word after the `!` is used as the command name (e.g. `!debug foo bar`
   → name `debug`, args `["foo", "bar"]`), matched case-insensitively against
   `ChatCommandRegistry`. An unknown name replies "not found" and stops there.
4. A scope is opened for the message; the player is looked up by Steam id and the
   command instance is created from that same scope through `ActivatorUtilities`,
   so constructor-injected scoped dependencies are fresh per message.
5. The player's live dino (species, growth, vitals) is read from the bridge and put on
   the `CommandContext`. A player with no spawned pawn simply has no stats.
6. `CanRun` gates admin-only commands; a non-zero `Cooldown` is enforced per player
   through `CommandCooldownService`.
7. The returned string is DM'd back to the player via `bridgeClient.DmAsync`, and the
   cooldown window starts.

## `CommandContext`

| Field | Description |
|---|---|
| `PlayerName` | Display name of the sender (empty string if the chat line had none). |
| `PlayerSteam` | Steam ID of the sender, also used as the DM target. |
| `PlayerId` | Domain player id. |
| `IsAdmin` | Whether the player is flagged admin; drives the default `CanRun`. |
| `PlayerSpecies` | Species of the player's live dino, or `null` when they have no pawn. |
| `PlayerGrowth` | Growth (0..1) of the live dino; `0` when they have no pawn. |
| `HealthData` | Health / hunger / thirst / stamina of the live dino. |
| `Arguments` | Everything after the command word, split on spaces. |

## Writing a new command

1. Create a class that inherits `ChatCommand`:

```csharp
namespace Isle.Api.Chat.Commands;

public class PingCommand : ChatCommand
{
    public override string Name { get; } = "ping";
    public override string Description { get; } = "Replies with pong.";
    public override bool IsAdminOnly { get; set; } = false;

    public override Task<string> ExecuteAsync(CommandContext context)
    {
        return Task.FromResult("pong");
    }
}
```

2. Register it by adding its type to `ChatCommandRegistry.RegisteredTypes`:

```csharp
public static IReadOnlyList<Type> RegisteredTypes { get; } =
[
    typeof(DebugCommand),
    typeof(PingCommand),
];
```

3. If your command needs a dependency (e.g. a repository or service), just
   add it as a constructor parameter — `ActivatorUtilities.CreateInstance`
   will resolve it from the DI container automatically.

4. Optionally override `Cooldown` to opt into per-player rate limiting.

That's it; no other wiring is required. The command becomes callable in-game
as `!ping`, and `!help` picks it up automatically.
