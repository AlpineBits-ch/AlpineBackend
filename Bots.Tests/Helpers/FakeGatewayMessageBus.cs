using Guild.Contracts;
using Guild.Contracts.Bus.Response;
using Identity.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Response;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Bots.Tests.Helpers;

/// <summary>
/// Hand-rolled no-op IMessageBus for Gateway/Handlers/*.cs and BotCommandEndpoint tests - mirrors
/// this repo's no-mocking-framework convention (Guild.Tests/Helpers/FakeMessageBus.cs).
/// </summary>
internal sealed class FakeGatewayMessageBus : IMessageBus
{
    public List<object> Invoked { get; } = new();

    public GetGuildMemberResponse GuildMemberResponse { get; set; } = new() { Member = null };
    public GetUserByIdResponse UserResponse { get; set; } = new() { User = null };
    public HasUserPermissionToChannelResponse PermissionResponse { get; set; } = new() { IsAllowed = true, Permission = ExternalPermission.SendMessages };
    public GetGuildSnapshotForBotResponse GuildSnapshotResponse { get; set; } = new() { Guild = null };
    public GetMessageResponse MessageResponse { get; set; } = new() { Message = null };

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);

        object response = message switch
        {
            _ when typeof(T) == typeof(GetGuildMemberResponse) => GuildMemberResponse,
            _ when typeof(T) == typeof(GetUserByIdResponse) => UserResponse,
            _ when typeof(T) == typeof(HasUserPermissionToChannelResponse) => PermissionResponse,
            _ when typeof(T) == typeof(GetGuildSnapshotForBotResponse) => GuildSnapshotResponse,
            _ when typeof(T) == typeof(GetMessageResponse) => MessageResponse,
            _ => throw new NotImplementedException($"FakeGatewayMessageBus has no canned response for {message.GetType().Name}"),
        };

        return Task.FromResult((T)response);
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);
        return Task.CompletedTask;
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
    public Guid? CorrelationId => null;
    public string? TenantId { get; set; }
    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => throw new NotImplementedException();
    public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken cancellation = default) => throw new NotImplementedException();
    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken cancellation = default) => throw new NotImplementedException();
    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => throw new NotImplementedException();
    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) => throw new NotImplementedException();
    public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => throw new NotImplementedException();
    public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) => throw new NotImplementedException();
    public IDestinationEndpoint EndpointFor(Uri uri) => throw new NotImplementedException();
    public IDestinationEndpoint EndpointFor(string endpointName) => throw new NotImplementedException();
}
