using System.Net.WebSockets;
using System.Threading.Channels;

namespace Bots.Tests.Helpers;

/// <summary>
/// Minimal in-memory test double for the abstract System.Net.WebSockets.WebSocket base class (no
/// interface exists to mock/fake against) - lets GatewayConnection be driven through its real
/// HELLO/IDENTIFY/heartbeat/receive-loop logic in a unit test without a real socket or an ASP.NET
/// Core TestServer.
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<QueuedMessage> _incoming = Channel.CreateUnbounded<QueuedMessage>();
    private WebSocketState _state = WebSocketState.Open;

    public List<string> SentTextMessages { get; } = new();
    public List<(WebSocketCloseStatus Status, string? Description)> Closes { get; } = new();

    private sealed record QueuedMessage(byte[] Data, bool IsClose);

    public void EnqueueText(string json) => _incoming.Writer.TryWrite(new QueuedMessage(System.Text.Encoding.UTF8.GetBytes(json), false));

    public void EnqueueClose() => _incoming.Writer.TryWrite(new QueuedMessage([], true));

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Closes.Add((closeStatus, statusDescription));
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Closes.Add((closeStatus, statusDescription));
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var message = await _incoming.Reader.ReadAsync(cancellationToken);
        if (message.IsClose)
        {
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "client closed");
        }

        var count = Math.Min(buffer.Count, message.Data.Length);
        Array.Copy(message.Data, 0, buffer.Array!, buffer.Offset, count);
        return new WebSocketReceiveResult(count, WebSocketMessageType.Text, true);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        SentTextMessages.Add(System.Text.Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        return Task.CompletedTask;
    }
}
