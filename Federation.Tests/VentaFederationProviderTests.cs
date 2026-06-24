using System.Net;
using System.Text.Json;
using AppEnvironment;
using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Application.Providers;
using Federation.Domain.Aggregates;
using NSec.Cryptography;

namespace Federation.Tests;

public class VentaFederationProviderTests
{
    // ── Entity ID helpers ─────────────────────────────────────────────────────
    // Format: localId:domain.com  (resolver prepends https:// automatically)

    private static class Id
    {
        public static string Channel(string domain)      => $"ch_test:{domain}";
        public static string Guild(string domain)        => $"gld_test:{domain}";
        public static string User(string domain)         => $"usr_test:{domain}";
        public static string Profile(string domain)      => $"prf_test:{domain}";
        public static string Friendship(string domain)   => $"frd_test:{domain}";
        public static string Conversation(string domain) => $"conv_test:{domain}";
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public record Captured(
            string Method,
            string? PathAndQuery,
            IReadOnlyDictionary<string, IEnumerable<string>> Headers,
            byte[] Body);

        public List<Captured> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null
                ? await request.Content.ReadAsByteArrayAsync(cancellationToken)
                : Array.Empty<byte>();

            Requests.Add(new Captured(
                request.Method.Method,
                request.RequestUri?.PathAndQuery,
                request.Headers.ToDictionary(h => h.Key, h => (IEnumerable<string>)h.Value),
                body));

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TestVentaFederationProvider : VentaFederationProvider
    {
        private readonly TestHttpMessageHandler _handler;

        public TestVentaFederationProvider(IFederatedDomainResolver resolver, TestHttpMessageHandler handler)
            : base(resolver) => _handler = handler;

        protected override HttpClient CreateHttpClient(Uri domain)
        {
            var client = new HttpClient(_handler, disposeHandler: false) { BaseAddress = domain };
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("X-Federated-Protocol", ProtocolVersion.ToString());
            return client;
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static (byte[] privateKey, byte[] publicKey) GenerateKeyPair()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        return (key.Export(KeyBlobFormat.PkixPrivateKeyText),
                key.PublicKey.Export(KeyBlobFormat.PkixPublicKeyText));
    }

    private static SignedFederationEvent DeserializeSignedEvent(byte[] body)
        => JsonSerializer.Deserialize<SignedFederationEvent>(body)!;

    private static T AssertPayloadType<T>(byte[] body) where T : FederationEvent
    {
        var signed = DeserializeSignedEvent(body);
        Assert.That(signed.Payload, Is.InstanceOf<T>(),
            $"Expected payload type {typeof(T).Name} but got {signed.Payload.GetType().Name}");
        return (T)signed.Payload;
    }

    private static void AssertPostToEventsEndpoint(TestHttpMessageHandler.Captured req)
    {
        Assert.That(req.Method, Is.EqualTo("POST"));
        Assert.That(req.PathAndQuery, Is.EqualTo("/api/v1/federation/events"));
        Assert.That(req.Headers.ContainsKey("X-Federated-Protocol"), Is.True);
        Assert.That(req.Headers["X-Federated-Protocol"].First(), Is.EqualTo("venta/v0.1"));
    }

    // =========================================================================
    [TestFixture]
    public class VentaDomainResolverTests
    {
        private VentaDomainResolver _resolver = null!;

        [SetUp]
        public void SetUp() => _resolver = new VentaDomainResolver();

        [Test]
        public async Task ResolveServerUrl_ValidFederatedId_ReturnsUri()
        {
            // Format: localId:domain.com — resolver prepends https://
            var result = await _resolver.ResolveServerUrlAsync(
                "channel:remote.example.com",
                new FederationProtocolVersion("venta", 0, 1));

            Assert.That(result, Is.EqualTo(new Uri("https://remote.example.com")));
        }

        [Test]
        public void ResolveServerUrl_MissingColon_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(() =>
                _resolver.ResolveServerUrlAsync(
                    "nodomain",
                    new FederationProtocolVersion("venta", 0, 1)).AsTask());
        }

        [Test]
        public void ResolveServerUrl_TrailingColon_ThrowsArgumentException()
        {
            Assert.ThrowsAsync<ArgumentException>(() =>
                _resolver.ResolveServerUrlAsync(
                    "channel:",
                    new FederationProtocolVersion("venta", 0, 1)).AsTask());
        }
    }

    // =========================================================================
    [TestFixture]
    public class OutboundMessagingTests
    {
        private TestHttpMessageHandler _handler = null!;
        private TestVentaFederationProvider _provider = null!;
        private const string Domain = "remote.example.com";

        [SetUp]
        public void SetUp()
        {
            var (priv, _) = GenerateKeyPair();
            Env.Federation.PrivateKey = priv;
            Env.GeneralConfiguration.InstanceUrl = "https://test.venta.gg";
            _handler = new TestHttpMessageHandler();
            _provider = new TestVentaFederationProvider(new VentaDomainResolver(), _handler);
        }

        [TearDown]
        public void TearDown() => _handler.Dispose();

        [Test]
        public async Task SendMessageAsync_PostsCorrectEvent()
        {
            var channelId = Id.Channel(Domain);
            await _provider.SendMessageAsync(channelId, "msg-1", "hello"u8.ToArray(), default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<MessageCreated>(_handler.Requests[0].Body);
            Assert.That(payload.MessageId, Is.EqualTo("msg-1"));
            Assert.That(payload.ChannelId, Is.EqualTo(channelId));
            Assert.That(payload.Content, Is.EqualTo("hello"u8.ToArray()));
        }

        [Test]
        public async Task EditMessageAsync_PostsCorrectEvent()
        {
            var channelId = Id.Channel(Domain);
            await _provider.EditMessageAsync(channelId, "msg-2", "edited"u8.ToArray(), default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<MessageEdited>(_handler.Requests[0].Body);
            Assert.That(payload.MessageId, Is.EqualTo("msg-2"));
            Assert.That(payload.Content, Is.EqualTo("edited"u8.ToArray()));
        }

        [Test]
        public async Task DeleteMessageAsync_PostsCorrectEvent()
        {
            var channelId = Id.Channel(Domain);
            await _provider.DeleteMessageAsync(channelId, "msg-3", default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<MessageDeleted>(_handler.Requests[0].Body);
            Assert.That(payload.MessageId, Is.EqualTo("msg-3"));
        }

        [Test]
        public async Task AddReactionAsync_PostsCorrectEvent()
        {
            var channelId = Id.Channel(Domain);
            await _provider.AddReactionAsync(channelId, "msg-4", "👍", default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<MessageReactionAdded>(_handler.Requests[0].Body);
            Assert.That(payload.MessageId, Is.EqualTo("msg-4"));
            Assert.That(payload.Emoji, Is.EqualTo("👍"));
        }

        [Test]
        public async Task RemoveReactionAsync_PostsCorrectEvent()
        {
            var channelId = Id.Channel(Domain);
            await _provider.RemoveReactionAsync(channelId, "msg-5", "❤️", default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<MessageReactionRemoved>(_handler.Requests[0].Body);
            Assert.That(payload.MessageId, Is.EqualTo("msg-5"));
            Assert.That(payload.Emoji, Is.EqualTo("❤️"));
        }
    }

    // =========================================================================
    [TestFixture]
    public class OutboundGuildTests
    {
        private TestHttpMessageHandler _handler = null!;
        private TestVentaFederationProvider _provider = null!;
        private const string Domain = "guild.example.com";

        [SetUp]
        public void SetUp()
        {
            var (priv, _) = GenerateKeyPair();
            Env.Federation.PrivateKey = priv;
            Env.GeneralConfiguration.InstanceUrl = "https://test.venta.gg";
            _handler = new TestHttpMessageHandler();
            _provider = new TestVentaFederationProvider(new VentaDomainResolver(), _handler);
        }

        [TearDown]
        public void TearDown() => _handler.Dispose();

        [Test]
        public async Task JoinChannelAsync_PostsCorrectEvent()
        {
            var guildId = Id.Guild(Domain);
            await _provider.JoinChannelAsync(guildId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<GuildMemberJoined>(_handler.Requests[0].Body);
            Assert.That(payload.GuildId, Is.EqualTo(guildId));
        }

        [Test]
        public async Task LeaveChannelAsync_PostsCorrectEvent()
        {
            var guildId = Id.Guild(Domain);
            await _provider.LeaveChannelAsync(guildId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<GuildMemberLeft>(_handler.Requests[0].Body);
            Assert.That(payload.GuildId, Is.EqualTo(guildId));
        }

        [Test]
        public async Task AcceptGuildInviteAsync_PostsCorrectEvent()
        {
            var guildId = Id.Guild(Domain);
            await _provider.AcceptGuildInviteAsync(guildId, "invite-abc", default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<GuildInviteAccepted>(_handler.Requests[0].Body);
            Assert.That(payload.GuildId, Is.EqualTo(guildId));
            Assert.That(payload.InviteCode, Is.EqualTo("invite-abc"));
        }

        [Test]
        public async Task RevokeGuildInviteAsync_PostsCorrectEvent()
        {
            var guildId = Id.Guild(Domain);
            await _provider.RevokeGuildInviteAsync(guildId, "invite-xyz", default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<GuildInviteRevoked>(_handler.Requests[0].Body);
            Assert.That(payload.GuildId, Is.EqualTo(guildId));
            Assert.That(payload.InviteCode, Is.EqualTo("invite-xyz"));
        }

        [Test]
        public async Task BanGuildMemberAsync_PostsCorrectEvent()
        {
            var guildId = Id.Guild(Domain);
            var bannedUserId = Id.User(Domain);
            await _provider.BanGuildMemberAsync(guildId, bannedUserId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<GuildMemberBanned>(_handler.Requests[0].Body);
            Assert.That(payload.GuildId, Is.EqualTo(guildId));
            Assert.That(payload.BannedUserId, Is.EqualTo(bannedUserId));
        }
    }

    // =========================================================================
    [TestFixture]
    public class OutboundSocialTests
    {
        private TestHttpMessageHandler _handler = null!;
        private TestVentaFederationProvider _provider = null!;
        private const string Domain = "social.example.com";

        [SetUp]
        public void SetUp()
        {
            var (priv, _) = GenerateKeyPair();
            Env.Federation.PrivateKey = priv;
            Env.GeneralConfiguration.InstanceUrl = "https://test.venta.gg";
            _handler = new TestHttpMessageHandler();
            _provider = new TestVentaFederationProvider(new VentaDomainResolver(), _handler);
        }

        [TearDown]
        public void TearDown() => _handler.Dispose();

        [Test]
        public async Task SendFriendRequestAsync_PostsCorrectEvent()
        {
            // Routed by user/profile ID to target's server
            var targetUserId = Id.Profile(Domain);
            await _provider.SendFriendRequestAsync(targetUserId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<SocialFriendRequest>(_handler.Requests[0].Body);
            Assert.That(payload.TargetUserId, Is.EqualTo(targetUserId));
        }

        [Test]
        public async Task AcceptFriendRequestAsync_PostsCorrectEvent()
        {
            // Routed by friendship/user ID back to initiator's server
            var initiatorUserId = Id.Friendship(Domain);
            await _provider.AcceptFriendRequestAsync(initiatorUserId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<SocialFriendAccepted>(_handler.Requests[0].Body);
            Assert.That(payload.InitiatorUserId, Is.EqualTo(initiatorUserId));
        }

        [Test]
        public async Task RejectFriendRequestAsync_PostsCorrectEvent()
        {
            var initiatorUserId = Id.Friendship(Domain);
            await _provider.RejectFriendRequestAsync(initiatorUserId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<SocialFriendRejected>(_handler.Requests[0].Body);
            Assert.That(payload.InitiatorUserId, Is.EqualTo(initiatorUserId));
        }

        [Test]
        public async Task RemoveFriendAsync_PostsCorrectEvent()
        {
            var targetUserId = Id.User(Domain);
            await _provider.RemoveFriendAsync(targetUserId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<SocialFriendRemoved>(_handler.Requests[0].Body);
            Assert.That(payload.TargetUserId, Is.EqualTo(targetUserId));
        }
    }

    // =========================================================================
    [TestFixture]
    public class OutboundConversationTests
    {
        private TestHttpMessageHandler _handler = null!;
        private TestVentaFederationProvider _provider = null!;
        private const string Domain = "conv.example.com";

        [SetUp]
        public void SetUp()
        {
            var (priv, _) = GenerateKeyPair();
            Env.Federation.PrivateKey = priv;
            Env.GeneralConfiguration.InstanceUrl = "https://test.venta.gg";
            _handler = new TestHttpMessageHandler();
            _provider = new TestVentaFederationProvider(new VentaDomainResolver(), _handler);
        }

        [TearDown]
        public void TearDown() => _handler.Dispose();

        [Test]
        public async Task CreateConversationAsync_WithFederatedMembers_SendsOneRequestPerFederatedMember()
        {
            var members = new[]
            {
                "localUser1",                           // local — no POST
                "localUser2",                           // local — no POST
                Id.User(Domain),                        // federated — POST
                Id.User("other.example.com")            // federated — POST
            };

            await _provider.CreateConversationAsync("conv-local-id", members, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(2),
                "Only federated members should receive a POST");
            foreach (var req in _handler.Requests)
                AssertPostToEventsEndpoint(req);
        }

        [Test]
        public async Task EditConversationAsync_PostsCorrectEvent()
        {
            var convId = Id.Conversation(Domain);
            await _provider.EditConversationAsync(convId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<ConversationEdited>(_handler.Requests[0].Body);
            Assert.That(payload.ConversationId, Is.EqualTo(convId));
        }

        [Test]
        public async Task DeleteConversationAsync_PostsCorrectEvent()
        {
            var convId = Id.Conversation(Domain);
            await _provider.DeleteConversationAsync(convId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<ConversationDeleted>(_handler.Requests[0].Body);
            Assert.That(payload.ConversationId, Is.EqualTo(convId));
        }

        [Test]
        public async Task AddConversationMemberAsync_PostsCorrectEvent()
        {
            var userId = Id.User(Domain);
            await _provider.AddConversationMemberAsync("conv-local-id", userId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<ConversationMemberAdded>(_handler.Requests[0].Body);
            Assert.That(payload.ConversationId, Is.EqualTo("conv-local-id"));
            Assert.That(payload.UserId, Is.EqualTo(userId));
        }

        [Test]
        public async Task RemoveConversationMemberAsync_PostsCorrectEvent()
        {
            var userId = Id.User(Domain);
            await _provider.RemoveConversationMemberAsync("conv-local-id", userId, default);

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
            AssertPostToEventsEndpoint(_handler.Requests[0]);
            var payload = AssertPayloadType<ConversationMemberLeft>(_handler.Requests[0].Body);
            Assert.That(payload.ConversationId, Is.EqualTo("conv-local-id"));
            Assert.That(payload.UserId, Is.EqualTo(userId));
        }
    }

    // =========================================================================
    [TestFixture]
    public class InboundHandlingTests
    {
        private VentaFederationProvider _provider = null!;

        [SetUp]
        public void SetUp() => _provider = new VentaFederationProvider(new VentaDomainResolver());

        [Test]
        public void HandleInboundEvent_CorrectProtocolVersion_FiresOnFederatedEventReceived()
        {
            FederationEvent? received = null;
            _provider.OnFederatedEventReceived += e => received = e;

            Assert.DoesNotThrowAsync(() =>
                _provider.HandleInboundEventAsync(new MessageCreated { ProtocolVersion = "venta/v0.1" }));
            Assert.That(received, Is.Not.Null);
        }

        [Test]
        public void HandleInboundEvent_CorrectProtocolVersion_EventIsPassedThrough()
        {
            FederationEvent? received = null;
            _provider.OnFederatedEventReceived += e => received = e;
            var @event = new MessageCreated { ProtocolVersion = "venta/v0.1", MessageId = "exact-msg-id" };

            Assert.DoesNotThrowAsync(() => _provider.HandleInboundEventAsync(@event));
            Assert.That(received, Is.SameAs(@event));
        }

        [Test]
        public void HandleInboundEvent_WrongProtocolVersion_ThrowsInvalidOperationException()
        {
            var @event = new MessageCreated { ProtocolVersion = "matrix/v1.0" };

            Assert.ThrowsAsync<InvalidOperationException>(() =>
                _provider.HandleInboundEventAsync(@event));
        }

        [Test]
        public void HandleInboundEvent_EmptyProtocolVersion_ThrowsInvalidOperationException()
        {
            var @event = new MessageCreated { ProtocolVersion = string.Empty };

            Assert.ThrowsAsync<InvalidOperationException>(() =>
                _provider.HandleInboundEventAsync(@event));
        }

        [Test]
        public void HandleInboundEvent_NoSubscribers_DoesNotThrow()
        {
            var @event = new MessageCreated { ProtocolVersion = "venta/v0.1" };

            Assert.DoesNotThrowAsync(() => _provider.HandleInboundEventAsync(@event));
        }
    }

    // =========================================================================
    [TestFixture]
    public class EventFieldTests
    {
        private TestHttpMessageHandler _handler = null!;
        private TestVentaFederationProvider _provider = null!;
        private const string Domain = "remote.example.com";
        private const string InstanceUrl = "https://test.venta.gg";

        [SetUp]
        public void SetUp()
        {
            var (priv, _) = GenerateKeyPair();
            Env.Federation.PrivateKey = priv;
            Env.GeneralConfiguration.InstanceUrl = InstanceUrl;
            _handler = new TestHttpMessageHandler();
            _provider = new TestVentaFederationProvider(new VentaDomainResolver(), _handler);
        }

        [TearDown]
        public void TearDown() => _handler.Dispose();

        private static void AssertEventFields(FederationEvent payload)
        {
            Assert.That(payload.EventId, Is.Not.Empty);
            Assert.That(Guid.TryParse(payload.EventId, out _), Is.True, "EventId must be a valid GUID");
            Assert.That(payload.OriginServerTime,
                Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(5)));
            Assert.That(payload.Host, Is.EqualTo(InstanceUrl));
            Assert.That(payload.ProtocolVersion, Is.EqualTo("venta/v0.1"));
        }

        [Test]
        public async Task MessagingEvent_HasCorrectEventFields()
        {
            await _provider.SendMessageAsync(Id.Channel(Domain), "m", Array.Empty<byte>(), default);
            AssertEventFields(AssertPayloadType<MessageCreated>(_handler.Requests[0].Body));
        }

        [Test]
        public async Task GuildEvent_HasCorrectEventFields()
        {
            await _provider.JoinChannelAsync(Id.Guild(Domain), default);
            AssertEventFields(AssertPayloadType<GuildMemberJoined>(_handler.Requests[0].Body));
        }

        [Test]
        public async Task SocialEvent_HasCorrectEventFields()
        {
            await _provider.SendFriendRequestAsync(Id.User(Domain), default);
            AssertEventFields(AssertPayloadType<SocialFriendRequest>(_handler.Requests[0].Body));
        }

        [Test]
        public async Task ConversationEvent_HasCorrectEventFields()
        {
            await _provider.EditConversationAsync(Id.Conversation(Domain), default);
            AssertEventFields(AssertPayloadType<ConversationEdited>(_handler.Requests[0].Body));
        }
    }

    // =========================================================================
    [TestFixture]
    public class SignatureTests
    {
        private byte[] _publicKeyBytes = null!;
        private TestHttpMessageHandler _handler = null!;
        private TestVentaFederationProvider _provider = null!;
        private const string Domain = "remote.example.com";

        [SetUp]
        public void SetUp()
        {
            var (priv, pub) = GenerateKeyPair();
            _publicKeyBytes = pub;
            Env.Federation.PrivateKey = priv;
            Env.GeneralConfiguration.InstanceUrl = "https://test.venta.gg";
            _handler = new TestHttpMessageHandler();
            _provider = new TestVentaFederationProvider(new VentaDomainResolver(), _handler);
        }

        [TearDown]
        public void TearDown() => _handler.Dispose();

        private FederationInstance BuildInstance(byte[] publicKey)
            => new() { PublicKey = publicKey, Host = "https://test.venta.gg", Name = "Test" };

        [Test]
        public async Task SignedEvent_CanBeVerified_WithGeneratedPublicKey()
        {
            await _provider.SendMessageAsync(Id.Channel(Domain), "sig-msg", "test"u8.ToArray(), default);

            var signed = DeserializeSignedEvent(_handler.Requests[0].Body);
            Assert.That(signed.IsValid(BuildInstance(_publicKeyBytes)), Is.True);
        }

        [Test]
        public async Task SignedEvent_FailsVerification_WithWrongPublicKey()
        {
            await _provider.SendMessageAsync(Id.Channel(Domain), "sig-msg", "test"u8.ToArray(), default);

            var signed = DeserializeSignedEvent(_handler.Requests[0].Body);
            var (_, wrongPublicKey) = GenerateKeyPair();
            Assert.That(signed.IsValid(BuildInstance(wrongPublicKey)), Is.False);
        }
    }
}
