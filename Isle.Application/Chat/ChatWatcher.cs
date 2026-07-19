using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;

namespace Isle.Api.Chat;

public class ChatWatcher(IChatStream chat, IEventStream events, ILogger<ChatWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (ChatMessage msg in chat.StreamAsync(ct))
        {
            logger.LogInformation("{UserName} with steam {Steam} wrote in chat {Text}", msg.Name, msg.Steam, msg.Text);
        }
    }
}