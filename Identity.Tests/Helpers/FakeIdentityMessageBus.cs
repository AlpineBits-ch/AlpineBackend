using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Identity.Tests.Helpers;

/// <summary>
/// Hand-rolled IMessageBus for endpoint unit tests, mirroring this repo's no-mocking-framework
/// convention (see Messaging.Tests/Helpers/FakeMessageBus.cs). Records every Invoke/Send/Publish
/// for assertions and resolves InvokeAsync&lt;T&gt; responses through an injected delegate. Every
/// unconfigured member throws, since nothing in this suite should reach it.
/// </summary>
internal class FakeIdentityMessageBus : IMessageBus
{
    public List<object> Invoked { get; } = new();
    public List<object> Sent { get; } = new();
    public List<object> Published { get; } = new();

    private readonly Func<object, object>? _responder;

    public FakeIdentityMessageBus(Func<object, object>? responder = null) => _responder = responder;

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);
        if (_responder is null)
            throw new InvalidOperationException($"FakeIdentityMessageBus has no responder for {message.GetType().Name}");

        return Task.FromResult((T)_responder(message));
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);
        return Task.CompletedTask;
    }

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message is not null) Sent.Add(message);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message is not null) Published.Add(message);
        return ValueTask.CompletedTask;
    }

    public Guid? CorrelationId => null;
    public string? TenantId { get; set; }

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
