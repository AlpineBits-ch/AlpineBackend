using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AppEnvironment;
using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Application.Providers;
using Federation.Application.Services;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

namespace Federation.Tests;

/// <summary>
/// End-to-end federation tests: Instance A (sender) posts signed events to Instance B (in-process
/// TestServer).
/// </summary>
[TestFixture]
public class FederationIntegrationTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string InstanceAHost   = "https://instance-a.example.com";
    private const string InstanceBDomain = "instance-b.example.com";

    // ── State ─────────────────────────────────────────────────────────────────

    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    private byte[] _instanceAPrivateKey = null!;
    private byte[] _instanceAPublicKey  = null!;

    private WebApplication              _serverBApp      = null!;
    private TestServer                  _testServer      = null!;
    private VentaFederationProvider     _serverBProvider = null!;
    private IntegrationTestProvider     _senderA         = null!;
    private readonly List<FederationEvent> _received     = new();

    // ── Sender subclass ───────────────────────────────────────────────────────

    // Routes all outbound HTTP through the in-process test-server handler
    // instead of opening real network connections.
    private sealed class IntegrationTestProvider : VentaFederationProvider
    {
        private readonly HttpMessageHandler _handler;

        public IntegrationTestProvider(IFederatedDomainResolver resolver, HttpMessageHandler handler)
            : base(resolver) => _handler = handler;

        protected override HttpClient CreateHttpClient(Uri domain)
        {
            var client = new HttpClient(_handler, disposeHandler: false) { BaseAddress = domain };
            client.DefaultRequestHeaders.Add("X-Federated-Protocol", ProtocolVersion.ToString());
            return client;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (byte[] priv, byte[] pub) GenerateKeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        return (key.Export(KeyBlobFormat.RawPrivateKey),
                key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    private static ByteArrayContent BuildSignedBody(FederationEvent payload, byte[] privateKeyBytes)
    {
        using var key    = Key.Import(Algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature    = Algorithm.Sign(key, payloadBytes);
        var signed       = new SignedFederationEvent { Payload = payload, Signature = signature };
        var json         = JsonSerializer.SerializeToUtf8Bytes(signed);
        var content      = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    // ── Server lifecycle ──────────────────────────────────────────────────────

    [OneTimeSetUp]
    public async Task StartServer()
    {
        // Configure Instance A's identity.
        (_instanceAPrivateKey, _instanceAPublicKey) = GenerateKeyPair();
        Env.Federation.PrivateKey            = _instanceAPrivateKey;
        Env.GeneralConfiguration.InstanceUrl = InstanceAHost;

        // Instance B's provider — we subscribe to its received-event hook.
        _serverBProvider = new VentaFederationProvider(new VentaDomainResolver());
        _serverBProvider.OnFederatedEventReceived += e => _received.Add(e);

        var dbName  = $"fed-integration-{Guid.NewGuid()}";
        var builder = WebApplication.CreateBuilder();

        // Use TestServer as the transport so no real port is bound.
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders()
                       .AddConsole()
                       .SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddRouting();
        builder.Services.AddDbContext<MicroserviceContext>(
            opts => opts.UseInMemoryDatabase(dbName));
        builder.Services.AddScoped<FederationDagService>();
        builder.Services.AddSingleton<IFederationProvider>(_serverBProvider);
        builder.Services.AddSingleton<IFederatedDomainResolver, VentaDomainResolver>();

        _serverBApp = builder.Build();

        // Mirrors FederationEndpoint.EventAsync logic (no Wolverine in tests).
        _serverBApp.MapPost("/api/v1/federation/events", async (
            HttpRequest request,
            MicroserviceContext db,
            IFederationProvider provider,
            FederationDagService dagService) =>
        {
            SignedFederationEvent? @event;
            try
            {
                @event = await JsonSerializer
                    .DeserializeAsync<SignedFederationEvent>(request.Body);
            }
            catch { return Results.BadRequest("Malformed body"); }

            if (@event is null) return Results.BadRequest("Empty body");

            var instance = await db.FederationInstances
                .FirstOrDefaultAsync(i => i.Host == @event.Payload.Host);

            if (instance is null)        return Results.BadRequest("Unknown federated host");
            if (instance.Status != FederationStatus.Active) return Results.StatusCode(403);
            if (!@event.IsValid(instance)) return Results.BadRequest("Invalid signature");

            var ready = await dagService.RecordAndResolveAsync(@event.Payload);
            foreach (var e in ready)
                await provider.HandleInboundEventAsync(e);
            return Results.Ok();
        });

        await _serverBApp.StartAsync();

        // Resolve the TestServer that was registered by UseTestServer().
        _testServer = (TestServer)_serverBApp.Services.GetRequiredService<IServer>();

        // Seed Instance A's identity into Instance B's DB.
        using var scope = _serverBApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        db.FederationInstances.Add(new FederationInstance
        {
            Id        = "fein_instance_a_test",
            Host      = InstanceAHost,
            Name      = "Instance A",
            PublicKey = _instanceAPublicKey,
            Status    = FederationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Instance A sender: routes HTTP through Instance B's in-process handler.
        _senderA = new IntegrationTestProvider(
            new VentaDomainResolver(),
            _testServer.CreateHandler());
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        await _serverBApp.StopAsync();
        await _serverBApp.DisposeAsync();
    }

    [SetUp]
    public void ClearReceivedEvents() => _received.Clear();

    // =========================================================================
    // Happy-path: Instance A sends → Instance B validates & fires the event
    // =========================================================================

    [Test]
    public async Task SendMessage_ValidSignature_InstanceBReceivesEvent()
    {
        var channelId = $"ch_test:{InstanceBDomain}";

        await _senderA.SendMessageAsync(channelId, "int-msg-1", "hello"u8.ToArray(), default);

        Assert.That(_received, Has.Count.EqualTo(1));
        Assert.That(_received[0], Is.InstanceOf<MessageCreated>());
        var payload = (MessageCreated)_received[0];
        Assert.That(payload.MessageId,       Is.EqualTo("int-msg-1"));
        Assert.That(payload.Host,            Is.EqualTo(InstanceAHost));
        Assert.That(payload.ProtocolVersion, Is.EqualTo("venta/v0.1"));
    }

    [Test]
    public async Task SendFriendRequest_ValidSignature_InstanceBReceivesEvent()
    {
        var targetId = $"usr_test:{InstanceBDomain}";

        await _senderA.SendFriendRequestAsync(targetId, default);

        Assert.That(_received, Has.Count.EqualTo(1));
        Assert.That(_received[0], Is.InstanceOf<SocialFriendRequest>());
        var payload = (SocialFriendRequest)_received[0];
        Assert.That(payload.TargetUserId, Is.EqualTo(targetId));
        Assert.That(payload.Host,         Is.EqualTo(InstanceAHost));
    }

    [Test]
    public async Task SendMessage_ProtocolVersion_IsVentaV01OnReceivedEvent()
    {
        await _senderA.SendMessageAsync(
            $"ch:{InstanceBDomain}", "pv-msg", Array.Empty<byte>(), default);

        Assert.That(_received, Has.Count.EqualTo(1));
        Assert.That(_received[0].ProtocolVersion, Is.EqualTo("venta/v0.1"));
    }

    // =========================================================================
    // Rejection: server returns error codes and fires no event
    // =========================================================================

    [Test]
    public async Task PostEvent_UnknownHost_ServerReturnsBadRequest()
    {
        var payload = new MessageCreated
        {
            Host             = "https://unknown.example.com",  // not seeded
            ProtocolVersion  = "venta/v0.1",
            EventId          = Guid.NewGuid().ToString(),
            MessageId        = "unknown-host-msg",
            OriginServerTime = DateTime.UtcNow,
        };

        using var content  = BuildSignedBody(payload, _instanceAPrivateKey);
        using var client   = _testServer.CreateClient();
        var response = await client.PostAsync("/api/v1/federation/events", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_received, Is.Empty);
    }

    [Test]
    public async Task PostEvent_WrongSignatureKey_ServerReturnsBadRequest()
    {
        // Host IS known, but the signature was created with a different key pair.
        var (wrongPriv, _) = GenerateKeyPair();

        var payload = new MessageCreated
        {
            Host             = InstanceAHost,
            ProtocolVersion  = "venta/v0.1",
            EventId          = Guid.NewGuid().ToString(),
            MessageId        = "bad-sig-msg",
            OriginServerTime = DateTime.UtcNow,
        };

        using var content  = BuildSignedBody(payload, wrongPriv);
        using var client   = _testServer.CreateClient();
        var response = await client.PostAsync("/api/v1/federation/events", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_received, Is.Empty);
    }

    [Test]
    public async Task PostEvent_DefederatedInstance_ServerReturnsForbidden()
    {
        const string blockedHost = "https://blocked.example.com";
        var (blockedPriv, blockedPub) = GenerateKeyPair();

        // Add the blocked instance to Instance B's DB.
        using (var scope = _serverBApp.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            db.FederationInstances.Add(new FederationInstance
            {
                Id        = "fein_blocked_test",
                Host      = blockedHost,
                Name      = "Blocked Instance",
                PublicKey = blockedPub,
                Status    = FederationStatus.Defederated,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var payload = new MessageCreated
        {
            Host             = blockedHost,
            ProtocolVersion  = "venta/v0.1",
            EventId          = Guid.NewGuid().ToString(),
            MessageId        = "blocked-msg",
            OriginServerTime = DateTime.UtcNow,
        };

        using var content  = BuildSignedBody(payload, blockedPriv);
        using var client   = _testServer.CreateClient();
        var response = await client.PostAsync("/api/v1/federation/events", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_received, Is.Empty);
    }

    // =========================================================================
    // DAG ordering: Instance B receives out-of-order events in causal order
    // =========================================================================

    [Test]
    public async Task SendMessages_OutOfOrder_InstanceBReceivesInCausalOrder()
    {
        var channelId = $"ch_order:{InstanceBDomain}";
        var m1Id = Guid.NewGuid().ToString();
        var m2Id = Guid.NewGuid().ToString();
        var m3Id = Guid.NewGuid().ToString();

        var m1 = new MessageCreated
        {
            EventId = m1Id, MessageId = "ooo-m1", ChannelId = channelId,
            Host = InstanceAHost, ProtocolVersion = "venta/v0.1",
            PreviousEventIds = [], Depth = 0,
            OriginServerTime = DateTime.UtcNow
        };
        var m2 = new MessageCreated
        {
            EventId = m2Id, MessageId = "ooo-m2", ChannelId = channelId,
            Host = InstanceAHost, ProtocolVersion = "venta/v0.1",
            PreviousEventIds = [m1Id], Depth = 1,
            OriginServerTime = DateTime.UtcNow
        };
        var m3 = new MessageCreated
        {
            EventId = m3Id, MessageId = "ooo-m3", ChannelId = channelId,
            Host = InstanceAHost, ProtocolVersion = "venta/v0.1",
            PreviousEventIds = [m2Id], Depth = 2,
            OriginServerTime = DateTime.UtcNow
        };

        using var client = _testServer.CreateClient();

        // Deliver out of order: M3, M1, M2
        await client.PostAsync("/api/v1/federation/events", BuildSignedBody(m3, _instanceAPrivateKey));
        await client.PostAsync("/api/v1/federation/events", BuildSignedBody(m1, _instanceAPrivateKey));
        await client.PostAsync("/api/v1/federation/events", BuildSignedBody(m2, _instanceAPrivateKey));

        Assert.That(_received, Has.Count.EqualTo(3));
        Assert.That(((MessageCreated)_received[0]).MessageId, Is.EqualTo("ooo-m1"));
        Assert.That(((MessageCreated)_received[1]).MessageId, Is.EqualTo("ooo-m2"));
        Assert.That(((MessageCreated)_received[2]).MessageId, Is.EqualTo("ooo-m3"));
    }
}
