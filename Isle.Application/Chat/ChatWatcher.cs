using Isle.Domain.Aggregates;
using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic;

namespace Isle.Api.Chat;

public class ChatWatcher(IChatStream chat, IEventStream events, ILogger<ChatWatcher> logger, IServiceScopeFactory scopeFactory,IBridgeClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        
        
        var chatTask = Task.Run(async () =>
        {
            try
            {
                await foreach (ChatMessage msg in chat.StreamAsync(ct))
                {
                    logger.LogInformation("{UserName} with steam {Steam} wrote in chat {Text}", 
                        msg.Name, msg.Steam, msg.Text);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred in chat stream");
            }
        }, ct);

        var eventTask = Task.Run(async () =>
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
                logger.LogInformation("Event stream started");
                try
                {
                    await foreach (GameEvent msg in events.StreamAsync(ct))
                    {
                        switch (msg.Kind)
                        {
                            case EventKind.Death:
                                logger.LogInformation("Player {Name} died", msg.Steam);
                                break;
                            case EventKind.Join:
                            {
                            
                                logger.LogInformation("Player {Name} joined", msg.Steam);
                              

                            
                                break;
                            }
                            case EventKind.Leave:
                                logger.LogInformation("Player {Name} left", msg.Steam);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occurred in event stream");
                }
            }
            
        }, ct);

        await Task.WhenAll(chatTask, eventTask);
    }
}