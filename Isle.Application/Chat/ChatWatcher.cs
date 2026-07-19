using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;

namespace Isle.Api.Chat;

public class ChatWatcher(IChatStream chat, IEventStream events) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (ChatMessage msg in chat.StreamAsync(ct))
        {
            Console.WriteLine(msg);
        }
    }
}