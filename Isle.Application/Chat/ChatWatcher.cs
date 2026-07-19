using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.VisualBasic;

namespace Isle.Api.Chat;

public class ChatWatcher(IChatStream chat, IEventStream events, ILogger<ChatWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (ChatMessage msg in chat.StreamAsync(ct))
        {
            logger.LogInformation("{UserName} with steam {Steam} wrote in chat {Text}", msg.Name, msg.Steam, msg.Text);
        }
        
        await foreach (GameEvent msg in events.StreamAsync(ct))
        {
            if (msg.Kind == EventKind.Death)
            {
                logger.LogInformation("Player {Name} died", msg.Steam);
            }
            if(msg.Kind == EventKind.Join)
            {
                logger.LogInformation("Player {Name} joined", msg.Steam);
            }
            if(msg.Kind == EventKind.Leave)
            {
                logger.LogInformation("Player {Name} left", msg.Steam);
            }
        }
    }
}