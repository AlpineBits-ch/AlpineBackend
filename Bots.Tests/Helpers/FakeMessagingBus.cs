using Guild.Contracts;
using Guild.Contracts.Bus.Response;
using Identity.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Wolverine;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Bots.Tests.Helpers;

/// <summary>Hand-rolled no-op IMessageBus for the Bots.Application/Endpoints/Discord/*.cs tests -
/// mirrors this repo's no-mocking-framework convention. Only the request/response pairs those
/// endpoints actually invoke (permission checks, message create/update, channel/member/user
/// lookups) return canned data; everything else throws since nothing in this suite should reach
/// it.</summary>
internal sealed class FakeMessagingBus : IMessageBus
{
    public List<object> Invoked { get; } = new();

    public HasUserPermissionToChannelResponse PermissionResponse { get; set; } =
        new() { IsAllowed = true, Permission = ExternalPermission.SendMessages };

    public Message MessageResponse { get; set; } = Message.Create(new CreateMessageParams
    {
        Content = "hi"u8.ToArray(), ChannelId = "ch_1", AuthorId = "usr_bot1", AuthorIdType = AuthorIdType.Bot,
    });

    public UpdateMessageResponse UpdateResponse { get; set; } = new() { Success = true, UpdatedAt = DateTimeOffset.UtcNow };
    public GetChannelResponse ChannelResponse { get; set; } = new() { Channel = null };
    public ListGuildMembersResponse MembersResponse { get; set; } = new();
    public GetUserByIdResponse UserResponse { get; set; } = new() { User = null };

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Invoked.Add(message);

        object response = message switch
        {
            _ when typeof(T) == typeof(HasUserPermissionToChannelResponse) => PermissionResponse,
            _ when typeof(T) == typeof(Message) => MessageResponse,
            _ when typeof(T) == typeof(UpdateMessageResponse) => UpdateResponse,
            _ when typeof(T) == typeof(GetChannelResponse) => ChannelResponse,
            _ when typeof(T) == typeof(ListGuildMembersResponse) => MembersResponse,
            _ when typeof(T) == typeof(GetUserByIdResponse) => UserResponse,
            _ => throw new NotImplementedException($"FakeMessagingBus has no canned response for {message.GetType().Name}"),
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
