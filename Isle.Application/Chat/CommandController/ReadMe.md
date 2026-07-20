# Chat Command System

A lightweight, extensible chat-command framework for handling in-game `!commands`
sent through the RCON/chat bridge. Commands are picked up as they stream in,
matched by name, executed, and the result is DM'd back to the player.

## Overview

The system has three main pieces:

| Component | Purpose |
|---|---|
| `CommandController` | A `BackgroundService` that listens to the chat stream, routes messages to the correct command, and replies via the bridge client. |
| `ChatCommand` | Abstract base class that every command implements. |
| `CommandContext` | The data passed into a command when it executes (player info, arguments, etc.). |

## How it works

1. On startup, `CommandController.ExecuteAsync` iterates `RegisteredTypes` and
   instantiates each one through `ActivatorUtilities.CreateInstance`, so
   commands can take constructor-injected dependencies from the DI container.
2. It then subscribes to `chat.StreamAsync(stoppingToken)` and listens for
   incoming `ChatMessage`s.
3. Any message that doesn't start with `!` is ignored.
4. The first word after the `!` is used as the command name (e.g. `!debug foo bar`
   → name `debug`, args `["foo", "bar"]`). It's matched case-sensitively
   against each registered command's `Name`.
5. If a match is found, a `CommandContext` is built and passed to
   `command.ExecuteAsync(context)`.
6. The returned string is sent back to the player as a direct message via
   `bridgeClient.DmAsync`.

Unhandled exceptions in the stream loop are caught and logged;
`OperationCanceledException` is swallowed silently on shutdown.

## `CommandContext`

The context object handed to every command:

```csharp
public class CommandContext
{
    public string PlayerName { get; set; }
    public string PlayerSteam { get; set; }
    public string PlayerSpecies { get; set; }
    public DinoHealthData HealthData { get; set; }
    public ICollection<string> Arguments { get; set; }
}
```

| Field | Description |
|---|---|
| `PlayerName` | Display name of the player who sent the command (falls back to empty string if the chat message had none). |
| `PlayerSteam` | Steam ID of the sender, also used as the DM target. |
| `PlayerSpecies` | The player's current in-game species. |
| `HealthData` | The player's dino health data. |
| `Arguments` | Everything after the command word, split on spaces. |

> **Note:** In the current implementation, `PlayerSpecies` and `HealthData`
> are placeholder values (`"Rex of course"` and `new DinoHealthData()`) rather
> than data pulled from the game state. Wire these up to a real data source
> before relying on them in a command.

## Writing a new command

1. Create a class that inherits `ChatCommand`:

```csharp
using Isle.Api.Chat.CommandController;

namespace Isle.Api.Chat.CommandController.Commands;

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

2. Register it by adding its type to `CommandController.RegisteredTypes`:

```csharp
public static ICollection<Type> RegisteredTypes { get; } =
[
    typeof(DebugCommand),
    typeof(PingCommand),
];
```

3. If your command needs a dependency (e.g. a repository or service), just
   add it as a constructor parameter — `ActivatorUtilities.CreateInstance`
   will resolve it from the DI container automatically.

That's it; no other wiring is required. The command becomes callable in-game
as `!ping`.

## Example: `DebugCommand`

A minimal reference implementation used to verify the pipeline works end to end:

```csharp
public class DebugCommand : ChatCommand
{
    public override string Name { get; } = "debug";
    public override string Description { get; } = "Debug command, to see if the game actually deals with commands";
    public override bool IsAdminOnly { get; set; } = false;

    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        return $"Debug command received for {context.PlayerName}";
    }
}
```

Typing `!debug` in chat replies with `Debug command received for <PlayerName>`.
