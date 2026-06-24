using System.Net.Http.Headers;
using System.Text.Json;
using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Application.Messages;
using Federation.Application.Services;
using Wolverine;

namespace Federation.Application.Providers;

public class VentaDomainResolver : IFederatedDomainResolver
{
    public ValueTask<Uri> ResolveServerUrlAsync(string federatedId, FederationProtocolVersion protocolVersion)
    {
        int colonIndex = federatedId.IndexOf(':');
        if (colonIndex == -1 || colonIndex == federatedId.Length - 1)
            throw new ArgumentException($"Identifier '{federatedId}' is missing a valid domain suffix component.");

        var host = federatedId[(colonIndex + 1)..];
        var uri = host.Contains("://") ? new Uri(host) : new Uri($"https://{host}");
        return ValueTask.FromResult(uri);
    }
}

public class VentaFederationProvider : IFederationProvider
{
    public FederationProtocolVersion ProtocolVersion { get; } = new("venta", 0, 1);

    private readonly IFederatedDomainResolver _domainResolver;
    private readonly FederationDagService? _dagService;
    private readonly IMessageBus? _messageBus;
    private const string FederationEventsPath = "/api/v1/federation/events";

    public event Action<FederationEvent>? OnFederatedEventReceived;

    public VentaFederationProvider(
        IFederatedDomainResolver domainResolver,
        FederationDagService? dagService = null,
        IMessageBus? messageBus = null)
    {
        _domainResolver = domainResolver;
        _dagService = dagService;
        _messageBus = messageBus;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task HandleInboundEventAsync(FederationEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.ProtocolVersion != ProtocolVersion.ToString())
            throw new InvalidOperationException(
                $"Protocol version mismatch: expected {ProtocolVersion}, got {@event.ProtocolVersion}");

        if (_dagService is not null)
        {
            var ready = await _dagService.RecordAndResolveAsync(@event, cancellationToken);
            foreach (var e in ready)
                await FireEventAsync(e);
        }
        else
        {
            await FireEventAsync(@event);
        }
    }

    public Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken)
        => Task.FromResult<object>(new { });

    // Messaging

    public async Task SendMessageAsync(string channelId, string messageId, byte[] content, CancellationToken cancellationToken)
    {
        var @event = new MessageCreated
        {
            MessageId = messageId,
            ChannelId = channelId,
            Content = content,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task EditMessageAsync(string channelId, string messageId, byte[] content, CancellationToken cancellationToken)
    {
        var @event = new MessageEdited
        {
            MessageId = messageId,
            ChannelId = channelId,
            Content = content,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken)
    {
        var @event = new MessageDeleted
        {
            MessageId = messageId,
            ChannelId = channelId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task AddReactionAsync(string channelId, string messageId, string reaction, CancellationToken cancellationToken)
    {
        var @event = new MessageReactionAdded
        {
            MessageId = messageId,
            ChannelId = channelId,
            Emoji = reaction,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task RemoveReactionAsync(string channelId, string messageId, string reaction, CancellationToken cancellationToken)
    {
        var @event = new MessageReactionRemoved
        {
            MessageId = messageId,
            ChannelId = channelId,
            Emoji = reaction,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    // Guild

    public async Task JoinChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var @event = new GuildMemberJoined
        {
            GuildId = channelId,
            ChannelId = channelId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        var @event = new GuildMemberLeft
        {
            GuildId = channelId,
            ChannelId = channelId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task AcceptGuildInviteAsync(string guildId, string inviteCode, CancellationToken cancellationToken)
    {
        var @event = new GuildInviteAccepted
        {
            GuildId = guildId,
            InviteCode = inviteCode,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(guildId, @event, cancellationToken);
    }

    public async Task RevokeGuildInviteAsync(string guildId, string inviteCode, CancellationToken cancellationToken)
    {
        var @event = new GuildInviteRevoked
        {
            GuildId = guildId,
            InviteCode = inviteCode,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(guildId, @event, cancellationToken);
    }

    public async Task BanGuildMemberAsync(string guildId, string userId, CancellationToken cancellationToken)
    {
        var @event = new GuildMemberBanned
        {
            GuildId = guildId,
            BannedUserId = userId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(guildId, @event, cancellationToken);
    }

    // Social

    public async Task SendFriendRequestAsync(string targetUserId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendRequest
        {
            TargetUserId = targetUserId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(targetUserId, @event, cancellationToken);
    }

    public async Task AcceptFriendRequestAsync(string sourceUserId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendAccepted
        {
            InitiatorUserId = sourceUserId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(sourceUserId, @event, cancellationToken);
    }

    public async Task RejectFriendRequestAsync(string sourceUserId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendRejected
        {
            InitiatorUserId = sourceUserId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(sourceUserId, @event, cancellationToken);
    }

    public async Task RemoveFriendAsync(string targetUserId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendRemoved
        {
            TargetUserId = targetUserId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(targetUserId, @event, cancellationToken);
    }

    // Conversation

    public async Task CreateConversationAsync(string conversationId, IEnumerable<string> memberIds, CancellationToken cancellationToken)
    {
        var members = memberIds.ToArray();
        var @event = new ConversationCreated
        {
            ConversationId = conversationId,
            MemberIds = members,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        foreach (var memberId in members.Where(IsFederated))
            await SendEventAsync(memberId, @event, cancellationToken);
    }

    public async Task EditConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        var @event = new ConversationEdited
        {
            ConversationId = conversationId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(conversationId, @event, cancellationToken);
    }

    public async Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        var @event = new ConversationDeleted
        {
            ConversationId = conversationId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(conversationId, @event, cancellationToken);
    }

    public async Task AddConversationMemberAsync(string conversationId, string userId, CancellationToken cancellationToken)
    {
        var @event = new ConversationMemberAdded
        {
            ConversationId = conversationId,
            UserId = userId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(userId, @event, cancellationToken);
    }

    public async Task RemoveConversationMemberAsync(string conversationId, string userId, CancellationToken cancellationToken)
    {
        var @event = new ConversationMemberLeft
        {
            ConversationId = conversationId,
            UserId = userId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(userId, @event, cancellationToken);
    }

    private async Task SendEventAsync(string federatedId, FederationEvent @event, CancellationToken cancellationToken)
    {
        var domain = await _domainResolver.ResolveServerUrlAsync(federatedId, ProtocolVersion);

        if (_dagService is not null)
        {
            var scopeKey = string.IsNullOrEmpty(@event.ChannelId) ? domain.Host : @event.ChannelId;
            await _dagService.StampAndRecordAsync(@event, scopeKey, cancellationToken);
        }

        using var client = CreateHttpClient(domain);
        var signed = SignedFederationEvent.Create(@event, ProtocolVersion.ToString());
        var json = JsonSerializer.SerializeToUtf8Bytes(signed);
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await client.PostAsync(FederationEventsPath, content, cancellationToken);
    }

    private async Task FireEventAsync(FederationEvent @event)
    {
        OnFederatedEventReceived?.Invoke(@event);
        if (_messageBus is not null)
            await _messageBus.PublishAsync(new FederationInboundEventReady(@event));
    }

    private static bool IsFederated(string id) => id.Contains(':');

    protected virtual HttpClient CreateHttpClient(Uri domain)
    {
        var client = new HttpClient { BaseAddress = domain };
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("X-Federated-Protocol", ProtocolVersion.ToString());
        return client;
    }
}
