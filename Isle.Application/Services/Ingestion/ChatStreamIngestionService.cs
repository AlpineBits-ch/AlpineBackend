using Isle.Contracts.Events.Chat;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Wolverine;

namespace Isle.Api.Services.Ingestion;

/// <summary>
/// The single consumer of the bridge chat feed. Every line becomes a
/// <see cref="ChatMessageReceivedEvent"/> on the bus; <c>!command</c> dispatch and any future chat
/// reactions are handlers on that event rather than additional stream subscribers.
/// </summary>
public sealed class ChatStreamIngestionService(
    IChatStream chatStream,
    IServiceScopeFactory scopeFactory,
    ILogger<ChatStreamIngestionService> logger)
    : BridgeStreamIngestionService<ChatMessage>(scopeFactory, logger)
{
    protected override string StreamName => "chat";

    protected override IAsyncEnumerable<ChatMessage> OpenStreamAsync(CancellationToken ct) =>
        chatStream.StreamAsync(ct);

    protected override Task PublishAsync(ChatMessage message, IMessageBus bus, CancellationToken ct)
    {
        logger.LogInformation("{UserName} with steam {Steam} wrote in chat {Text}",
            message.Name, message.Steam, message.Text);

        return bus.PublishAsync(new ChatMessageReceivedEvent
        {
            SteamId = message.Steam,
            Name = message.Name,
            Text = message.Text,
            Mode = message.Mode,
            TimestampMs = message.Ts,
        }).AsTask();
    }
}
