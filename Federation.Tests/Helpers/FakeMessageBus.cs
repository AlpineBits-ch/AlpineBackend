using Guild.Contracts.Bus.Response;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Federation.Tests.Helpers;

/// <summary>
/// Hand-rolled no-op IMessageBus for MessagingOutboundHandlers/InboundEventDispatcher tests -
/// mirrors this repo's no-mocking-framework convention (Guild.Tests/Helpers/FakeMessageBus.cs,
/// Import.Tests/Helpers/FakeSyncMessageBus.cs).
/// </summary>
public class FakeMessageBus : IMessageBus
{
    public List<object> Published { get; } = new();
    public List<object> Invoked { get; } = new();

    /// <summary>What InvokeAsync&lt;GetChannelResponse&gt; should return - null Channel by default
    /// (mirrors "channel not found / not linked").</summary>
    public GetChannelResponse ChannelResponse { get; set; } = new() { Channel = null };

    /// <summary>What InvokeAsync&lt;GetProfileByUserIdResponse&gt; should return - used by
    /// UserService.GetUserProfile on a cache miss.</summary>
    public GetProfileByUserIdResponse ProfileResponse { get; set; } = new() { Profile = null };

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);

        object response = message switch
        {
            _ when typeof(T) == typeof(GetChannelResponse) => ChannelResponse,
            _ when typeof(T) == typeof(GetProfileByUserIdResponse) => ProfileResponse,
            _ => throw new NotImplementedException($"FakeMessageBus has no canned response for {message.GetType().Name}"),
        };

        return Task.FromResult((T)response);
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);
        return Task.CompletedTask;
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        Published.Add(message!);
        return ValueTask.CompletedTask;
    }

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
