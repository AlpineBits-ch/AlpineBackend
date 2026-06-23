using Federation.Application.Dtos.Events;

namespace Federation.Application.Providers;

public class VentaDomainResolver : IFederatedDomainResolver
{
    public ValueTask<Uri> ResolveServerUrlAsync(string federatedId, FederationProtocolVersion protocolVersion)
    {
        int colonIndex = federatedId.IndexOf(':');
        if (colonIndex == -1 || colonIndex == federatedId.Length - 1)
            throw new ArgumentException($"Identifier '{federatedId}' is missing a valid domain suffix component.");

        return ValueTask.FromResult(new Uri(federatedId[(colonIndex + 1)..]));
    }
}

public class VentaFederationProvider : IFederationProvider
{
    public FederationProtocolVersion ProtocolVersion { get; } = new("venta", 0, 1);

    private readonly IFederatedDomainResolver _domainResolver;
    private readonly string _federationPath = "/.well-known/federation/events";

    public event Action<FederationEvent>? OnFederatedEventReceived;

    public VentaFederationProvider(IFederatedDomainResolver domainResolver)
    {
        _domainResolver = domainResolver;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task HandleInboundEventAsync(FederationEvent @event, CancellationToken cancellationToken = default)
    {
        OnFederatedEventReceived?.Invoke(@event);
        return Task.CompletedTask;
    }

    // Messaging
    public async Task SendMessageAsync(string channelId, byte[] content, CancellationToken cancellationToken)
    {
        var domain = await _domainResolver.ResolveServerUrlAsync(channelId, ProtocolVersion);
        using var client = CreateHttpClient(domain);
        await client.PutAsync(_federationPath, new ByteArrayContent(content), cancellationToken);
    }

    public Task EditMessageAsync(string channelId, string messageId, byte[] content, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task AddReactionAsync(string channelId, string messageId, string reaction, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task RemoveReactionAsync(string channelId, string messageId, string reaction, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    // Guild
    public async Task JoinChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var domain = await _domainResolver.ResolveServerUrlAsync(channelId, ProtocolVersion);
        using var client = CreateHttpClient(domain);
        await client.PutAsync(_federationPath, new StringContent(channelId), cancellationToken);
    }

    public async Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var domain = await _domainResolver.ResolveServerUrlAsync(channelId, ProtocolVersion);
        using var client = CreateHttpClient(domain);
        await client.PutAsync(_federationPath, new StringContent(channelId), cancellationToken);
    }

    public Task AcceptGuildInviteAsync(string guildId, string inviteCode, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task RevokeGuildInviteAsync(string guildId, string inviteCode, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task BanGuildMemberAsync(string guildId, string userId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    // Social
    public Task SendFriendRequestAsync(string targetUserId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task AcceptFriendRequestAsync(string sourceUserId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task RejectFriendRequestAsync(string sourceUserId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task RemoveFriendAsync(string targetUserId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    // Conversation
    public Task CreateConversationAsync(string conversationId, IEnumerable<string> memberIds, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task EditConversationAsync(string conversationId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task AddConversationMemberAsync(string conversationId, string userId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task RemoveConversationMemberAsync(string conversationId, string userId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    private HttpClient CreateHttpClient(Uri domain)
    {
        var client = new HttpClient { BaseAddress = domain };
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("X-Federated-Protocol", ProtocolVersion.ToString());
        return client;
    }
}
