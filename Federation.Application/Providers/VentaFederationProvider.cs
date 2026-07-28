using System.Net.Http.Headers;
using System.Text.Json;
using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Application.Bus.Inbound;
using Federation.Application.Messages;
using Federation.Application.Services;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    private readonly UserService? _userService;
    private readonly MicroserviceContext? _db;
    private const string FederationEventsPath = "/api/v1/federation/events";

    public event Action<FederationEvent>? OnFederatedEventReceived;

    public VentaFederationProvider(
        IFederatedDomainResolver domainResolver,
        FederationDagService? dagService = null,
        IMessageBus? messageBus = null,
        UserService? userService = null,
        MicroserviceContext? db = null)
    {
        _domainResolver = domainResolver;
        _dagService = dagService;
        _messageBus = messageBus;
        _userService = userService;
        _db = db;
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

    public async Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken)
    {
        if (_userService is null) return new { };
        var profile = await _userService.GetUserProfile(userId);
        return profile is not null ? profile : new { };
    }

    public async Task RetryUndeliveredEventsAsync(CancellationToken cancellationToken = default)
    {
        if (_dagService is null) return;

        var undelivered = await _dagService.GetUndeliveredAsync(maxAttempts: 10, cancellationToken);
        foreach (var record in undelivered)
        {
            if (record.TargetHost is null) continue; // shouldn't happen - Delivered defaults true for inbound rows, which never set TargetHost.

            var @event = JsonSerializer.Deserialize<FederationEvent>(record.PayloadJson)!;
            await PostSignedEventAsync(new Uri(record.TargetHost), @event, cancellationToken);
        }
    }

    // Messaging

    public async Task SendMessageAsync(string channelId, string messageId, byte[] content, string senderId, CancellationToken cancellationToken)
    {
        var @event = new MessageCreated
        {
            MessageId = messageId,
            ChannelId = channelId,
            Content = content,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task EditMessageAsync(string channelId, string messageId, byte[] content, string senderId, CancellationToken cancellationToken)
    {
        var @event = new MessageEdited
        {
            MessageId = messageId,
            ChannelId = channelId,
            Content = content,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task DeleteMessageAsync(string channelId, string messageId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new MessageDeleted
        {
            MessageId = messageId,
            ChannelId = channelId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task AddReactionAsync(string channelId, string messageId, string reaction, string senderId, CancellationToken cancellationToken)
    {
        var @event = new MessageReactionAdded
        {
            MessageId = messageId,
            ChannelId = channelId,
            Emoji = reaction,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task RemoveReactionAsync(string channelId, string messageId, string reaction, string senderId, CancellationToken cancellationToken)
    {
        var @event = new MessageReactionRemoved
        {
            MessageId = messageId,
            ChannelId = channelId,
            Emoji = reaction,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    // Guild

    public async Task JoinChannelAsync(string channelId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new GuildMemberJoined
        {
            GuildId = channelId,
            ChannelId = channelId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task LeaveChannelAsync(string channelId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new GuildMemberLeft
        {
            GuildId = channelId,
            ChannelId = channelId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(channelId, @event, cancellationToken);
    }

    public async Task AcceptGuildInviteAsync(string guildId, string inviteCode, string senderId, CancellationToken cancellationToken)
    {
        var @event = new GuildInviteAccepted
        {
            GuildId = guildId,
            InviteCode = inviteCode,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(guildId, @event, cancellationToken);
    }

    public async Task RevokeGuildInviteAsync(string guildId, string inviteCode, string senderId, CancellationToken cancellationToken)
    {
        var @event = new GuildInviteRevoked
        {
            GuildId = guildId,
            InviteCode = inviteCode,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(guildId, @event, cancellationToken);
    }

    public async Task BanGuildMemberAsync(string guildId, string userId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new GuildMemberBanned
        {
            GuildId = guildId,
            BannedUserId = userId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(guildId, @event, cancellationToken);
    }

    // Social

    public async Task SendFriendRequestAsync(string targetUserId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendRequest
        {
            TargetUserId = targetUserId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(targetUserId, @event, cancellationToken);
    }

    public async Task AcceptFriendRequestAsync(string sourceUserId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendAccepted
        {
            InitiatorUserId = sourceUserId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(sourceUserId, @event, cancellationToken);
    }

    public async Task RejectFriendRequestAsync(string sourceUserId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendRejected
        {
            InitiatorUserId = sourceUserId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(sourceUserId, @event, cancellationToken);
    }

    public async Task RemoveFriendAsync(string targetUserId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new SocialFriendRemoved
        {
            TargetUserId = targetUserId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(targetUserId, @event, cancellationToken);
    }

    // Conversation

    public async Task CreateConversationAsync(string conversationId, IEnumerable<string> memberIds, string senderId, CancellationToken cancellationToken)
    {
        var members = memberIds.ToArray();
        var @event = new ConversationCreated
        {
            ConversationId = conversationId,
            MemberIds = members,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        foreach (var memberId in members.Where(IsFederated))
            await SendEventAsync(memberId, @event, cancellationToken);
    }

    public async Task EditConversationAsync(string conversationId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new ConversationEdited
        {
            ConversationId = conversationId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(conversationId, @event, cancellationToken);
    }

    public async Task DeleteConversationAsync(string conversationId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new ConversationDeleted
        {
            ConversationId = conversationId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(conversationId, @event, cancellationToken);
    }

    public async Task AddConversationMemberAsync(string conversationId, string userId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new ConversationMemberAdded
        {
            ConversationId = conversationId,
            UserId = userId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(userId, @event, cancellationToken);
    }

    public async Task RemoveConversationMemberAsync(string conversationId, string userId, string senderId, CancellationToken cancellationToken)
    {
        var @event = new ConversationMemberLeft
        {
            ConversationId = conversationId,
            UserId = userId,
            SenderId = senderId,
            EventId = Guid.NewGuid().ToString(),
            OriginServerTime = DateTime.UtcNow
        };
        await SendEventAsync(userId, @event, cancellationToken);
    }

    private async Task SendEventAsync(string federatedId, FederationEvent @event, CancellationToken cancellationToken)
    {
        var domain = await _domainResolver.ResolveServerUrlAsync(federatedId, ProtocolVersion);

        // Defederated/Blocked always wins over any concurrent event processing - checked inbound
        // already (FederationEndpoint), but nothing previously checked it outbound, so a send to
        // a target we've since defederated/suspended would still fire (and get rejected 403 by
        // them, burning a delivery attempt, if it fires at all before the retry service catches
        // it - just quietly wrong instead of clearly a no-op).
        if (_db is not null)
        {
            var targetHost = domain.GetLeftPart(UriPartial.Authority);
            var target = await _db.FederationInstances.FirstOrDefaultAsync(i => i.Host == targetHost, cancellationToken);
            if (target is not null && target.Status != FederationStatus.Active)
                return;
        }

        if (_dagService is not null)
        {
            var scopeKey = string.IsNullOrEmpty(@event.ChannelId) ? domain.Host : @event.ChannelId;
            // The full scheme+host+port (not just Uri.Host, which drops the port) - needed to
            // reconstruct a valid destination Uri for a retry later.
            await _dagService.StampAndRecordAsync(@event, scopeKey, domain.GetLeftPart(UriPartial.Authority), cancellationToken);
        }

        await PostSignedEventAsync(domain, @event, cancellationToken);
    }

    // Previously fire-and-forget: the POST result was never checked, so a non-2xx (remote
    // Suspended, a transient network blip, the remote restarting) silently dropped the event
    // forever even though StampAndRecordAsync had already advanced the local DAG tip - the remote
    // side ends up with a permanent gap with no indication anything went wrong.
    private async Task PostSignedEventAsync(Uri domain, FederationEvent @event, CancellationToken cancellationToken)
    {
        bool delivered;
        try
        {
            using var client = CreateHttpClient(domain);
            var signed = SignedFederationEvent.Create(@event, ProtocolVersion.ToString());
            var json = JsonSerializer.SerializeToUtf8Bytes(signed);
            var content = new ByteArrayContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var response = await client.PostAsync(FederationEventsPath, content, cancellationToken);
            delivered = response.IsSuccessStatusCode;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            delivered = false;
        }

        if (_dagService is null) return;
        if (delivered)
            await _dagService.MarkDeliveredAsync(@event.EventId, cancellationToken);
        else
            await _dagService.MarkDeliveryFailedAsync(@event.EventId, cancellationToken);
    }

    private async Task FireEventAsync(FederationEvent @event)
    {
        OnFederatedEventReceived?.Invoke(@event);

        // Previously published FederationInboundEventReady here, which had no subscriber anywhere -
        // a successfully DAG-resolved inbound event was recorded and then silently dropped.
        if (_messageBus is not null && _db is not null)
            await InboundEventDispatcher.DispatchAsync(@event, _db, _messageBus, CancellationToken.None);
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
