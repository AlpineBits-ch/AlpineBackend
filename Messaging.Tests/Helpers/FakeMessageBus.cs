using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Messaging.Tests.Helpers;

/// <summary>
/// Hand-rolled configurable IMessageBus for endpoint/handler unit tests - mirrors this repo's
/// no-mocking-framework convention (see Guild.Tests/Helpers/FakeMessageBus.cs). Records every
/// Invoke/Send/Publish call for assertions, and resolves InvokeAsync&lt;T&gt; responses via an
/// injected delegate so each test can supply exactly the canned cross-service response(s) it
/// needs (e.g. HasUserPermissionToChannelResponse, GetGuildEmojiResponse). Every unconfigured
/// member throws since nothing in this suite should reach it.
/// </summary>
public class FakeMessageBus : IMessageBus
{
    public List<object> Invoked { get; } = new();
    public List<object> Sent { get; } = new();
    public List<object> Published { get; } = new();

    /// <summary>Exposed so a decorating bus can answer the few request types it cares about and
    /// hand everything else back to this one - see TestPrivacyServices, which layers the privacy
    /// and block lookups over a test's existing responder without every test having to restate
    /// them.</summary>
    public Func<object, object>? Responder { get; set; }

    public FakeMessageBus(Func<object, object>? responder = null) => Responder = responder;

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);
        if (Responder is null)
            throw new InvalidOperationException($"FakeMessageBus has no responder configured for {message.GetType().Name}");

        return Task.FromResult((T)Responder(message));
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
