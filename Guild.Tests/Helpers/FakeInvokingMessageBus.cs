using JasperFx.Core;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Guild.Tests.Helpers;

/// <summary>
/// Extends FakeMessageBus's no-op approach to also support request/response InvokeAsync&lt;T&gt;
/// calls (e.g. GuildEndpoint/GuildTemplateEndpoint's synchronous "ask Social for the profile"
/// round-trip), which the plain FakeMessageBus deliberately leaves throwing NotImplementedException
/// since most handler tests never reach it. Canned responses are keyed by request message type.
/// </summary>
public class FakeInvokingMessageBus : IMessageBus
{
    private readonly Dictionary<Type, object> _responses = new();

    public List<object> Published { get; } = new();
    public List<object> Invoked { get; } = new();

    /// <summary>Registers the object to return the next time InvokeAsync&lt;T&gt; is called with a
    /// message of type TRequest.</summary>
    public void SetResponse<TRequest>(object response) => _responses[typeof(TRequest)] = response;

    /// <summary>Drops every canned response, so the next InvokeAsync throws.</summary>
    public void ClearResponses() => _responses.Clear();

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message is not null) Published.Add(message);
        return ValueTask.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);
        if (_responses.TryGetValue(message.GetType(), out var response)) return Task.FromResult((T)response);
        throw new InvalidOperationException($"FakeInvokingMessageBus has no canned response for {message.GetType()}");
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);
        return Task.CompletedTask;
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
