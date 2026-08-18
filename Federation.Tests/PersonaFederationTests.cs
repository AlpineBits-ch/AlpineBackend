using System.Net;
using AppEnvironment;
using Federation.Application.Bus.Inbound;
using Federation.Application.Bus.Outbound;
using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Providers;
using Federation.Application.Services;
using Federation.Contracts.Materialization.Messaging;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Federation.Tests.Helpers;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;

namespace Federation.Tests;

/// <summary>
/// A persona is display data on the message, not an entity either side owns - these pin that it
/// survives the instance boundary and that nothing about it is materialized as a row.
/// </summary>
[TestFixture]
public class PersonaFederationTests
{
    private const string HomeHost = "https://home.example.com";
    private const string RemoteHost = "https://remote.example.com";

    private FakeFederationProvider _provider = null!;
    private UserService _userService = null!;
    private MicroserviceContext _db = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        Env.GeneralConfiguration.InstanceUrl = HomeHost;
        Env.Federation.PrivateKey = GenerateKeyPair().privateKey;

        _provider = new FakeFederationProvider();

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        var cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _bus = new FakeMessageBus();
        _userService = new UserService(cache, _bus);

        var opts = new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase($"persona-{Guid.NewGuid()}")
            .Options;
        _db = new MicroserviceContext(opts);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    // ── Outbound ─────────────────────────────────────────────────────────────

    [Test]
    public async Task MessageCreated_PersonaAuthor_CarriesTheCharacterOutbound()
    {
        await SeedLinkedGuildAsync();
        var message = PersonaMessage();

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.AuthorDisplays, Has.Count.EqualTo(1));
        var author = _provider.AuthorDisplays[0];
        Assert.Multiple(() =>
        {
            Assert.That(author.DisplayName, Is.EqualTo("Kaelen the Grey"));
            Assert.That(author.AvatarUrl, Is.EqualTo("https://home.example.com/media/kaelen.png"));
            Assert.That(author.AuthorIdType, Is.EqualTo(FederatedAuthorIdType.Persona));
        });

        // The account behind the character is what every authorization decision keys on, so it is
        // the account - not the persona - that is federated as the sender.
        Assert.That(_provider.Calls[0].Args[2], Is.EqualTo($"usr_writer:{HomeHost}"));
    }

    [Test]
    public async Task MessageUpdated_PersonaAuthor_CarriesTheCharacterOutbound()
    {
        await SeedLinkedGuildAsync();
        var message = new MessageUpdatedForChannel
        {
            ChannelId = "ch_1",
            MessageId = "m1",
            AuthorId = "usr_writer",
            Content = "he turns"u8.ToArray(),
            AuthorDisplayName = "Kaelen the Grey",
            AuthorAvatarUrl = "https://home.example.com/media/kaelen.png",
            PersonaId = "psn_kaelen",
            AuthorIdType = Guild.Contracts.Bus.Events.AuthorIdType.Persona,
        };

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.Calls[0].Method, Is.EqualTo(nameof(_provider.EditMessageAsync)));
        Assert.That(_provider.AuthorDisplays[0].DisplayName, Is.EqualTo("Kaelen the Grey"));
        Assert.That(_provider.AuthorDisplays[0].AuthorIdType, Is.EqualTo(FederatedAuthorIdType.Persona));
    }

    [Test]
    public async Task MessageCreated_OrdinaryUser_SendsNoOverrides()
    {
        await SeedLinkedGuildAsync();
        var message = new MessageCreatedForChannel
        {
            ChannelId = "ch_1", MessageId = "m1", AuthorId = "usr_writer", Content = "hi"u8.ToArray(),
        };

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.AuthorDisplays[0], Is.EqualTo(FederatedAuthorDisplay.RealUser));
    }

    // ── Loop prevention ──────────────────────────────────────────────────────

    [Test]
    public async Task MessageCreated_ArrivedFromAnotherInstance_IsNotFederatedBack()
    {
        await SeedLinkedGuildAsync();
        var message = PersonaMessage();
        message.AuthorId = "usr_writer:origin.example.com";

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        Assert.That(_provider.Calls, Is.Empty);
        Assert.That(_bus.Invoked, Is.Empty, "A foreign author short-circuits before the channel lookup");
    }

    [Test]
    public async Task MessageCreated_LocalPersonaId_DoesNotLookForeign()
    {
        await SeedLinkedGuildAsync();
        var message = PersonaMessage();

        await MessagingOutboundHandlers.Handle(message, _provider, _userService, _db, _bus, default);

        // Echo detects a foreign id by the colon in <localId>:<domain>. A local persona id is
        // unqualified, so carrying one must not make its message look like an inbound echo.
        Assert.That(message.PersonaId, Does.Not.Contain(":"));
        Assert.That(_provider.Calls, Has.Count.EqualTo(1));
    }

    // ── Round trip ───────────────────────────────────────────────────────────

    [Test]
    public async Task PersonaMessage_SurvivesTheWire_FromOutboundHandlerToMaterializationCommand()
    {
        await SeedLinkedGuildAsync();

        var wire = await SendOverTheWireAsync(PersonaMessage());

        Assert.Multiple(() =>
        {
            Assert.That(wire.AuthorDisplayName, Is.EqualTo("Kaelen the Grey"));
            Assert.That(wire.AuthorAvatarUrl, Is.EqualTo("https://home.example.com/media/kaelen.png"));
            Assert.That(wire.AuthorIdType, Is.EqualTo(FederatedAuthorIdType.Persona));
        });

        var received = await DispatchInboundAsync(wire);

        Assert.Multiple(() =>
        {
            Assert.That(received.AuthorDisplayName, Is.EqualTo("Kaelen the Grey"));
            Assert.That(received.AuthorAvatarUrl, Is.EqualTo("https://home.example.com/media/kaelen.png"));
            Assert.That(received.AuthorIdType, Is.EqualTo(FederatedAuthorIdType.Persona));
            Assert.That(received.SenderId, Is.EqualTo($"usr_writer:{HomeHost}"));
            Assert.That(received.ChannelId, Is.EqualTo("ch_1"), "The :remote suffix is stripped back off");
        });
    }

    [Test]
    public async Task OrdinaryMessage_ArrivesAsAPlainUserPost()
    {
        await SeedLinkedGuildAsync();
        var message = new MessageCreatedForChannel
        {
            ChannelId = "ch_1", MessageId = "m1", AuthorId = "usr_writer", Content = "hi"u8.ToArray(),
        };

        var received = await DispatchInboundAsync(await SendOverTheWireAsync(message));

        Assert.Multiple(() =>
        {
            Assert.That(received.AuthorDisplayName, Is.Null);
            Assert.That(received.AuthorAvatarUrl, Is.Null);
            Assert.That(received.AuthorIdType, Is.EqualTo(FederatedAuthorIdType.User));
        });
    }

    [Test]
    public void MaterializationCommand_HasNoPersonaId_SoNoDanglingReferenceCanBeStored()
    {
        // The receiving instance can neither authorize nor approve another instance's persona, so
        // the id deliberately never reaches the wire and Message.PersonaId stays null there.
        Assert.Multiple(() =>
        {
            Assert.That(typeof(FederatedMessageCreatedReceived).GetProperty("PersonaId"), Is.Null);
            Assert.That(typeof(FederatedMessageEditedReceived).GetProperty("PersonaId"), Is.Null);
            Assert.That(typeof(MessageCreated).GetProperty("PersonaId"), Is.Null);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MessageCreatedForChannel PersonaMessage() => new()
    {
        ChannelId = "ch_1",
        MessageId = "m1",
        AuthorId = "usr_writer",
        Content = "he draws his blade"u8.ToArray(),
        AuthorDisplayName = "Kaelen the Grey",
        AuthorAvatarUrl = "https://home.example.com/media/kaelen.png",
        PersonaId = "psn_kaelen",
        AuthorIdType = Guild.Contracts.Bus.Events.AuthorIdType.Persona,
    };

    private async Task SeedLinkedGuildAsync()
    {
        var instance = new FederationInstance { Id = "fein_remote", Host = RemoteHost, Name = RemoteHost, Status = FederationStatus.Active };
        _db.FederationInstances.Add(instance);
        _db.FederatedResources.Add(new FederatedResource
        {
            Id = FederatedResource.GenerateId(), LocalId = "gld_1", RemoteId = "gld_1",
            ResourceType = FederatedResourceType.Guild, InstanceId = instance.Id,
        });
        await _db.SaveChangesAsync();

        _bus.ChannelResponse = new GetChannelResponse
        {
            Channel = new ChannelInfo { Id = "ch_1", GuildId = "gld_1", Name = "tavern", Type = "Text" },
        };
    }

    /// <summary>Runs the outbound handler through the real provider and returns what came off the
    /// signed HTTP body, so the assertion is about the wire and not about an in-process object.</summary>
    private async Task<MessageCreated> SendOverTheWireAsync(MessageCreatedForChannel message)
    {
        using var handler = new CapturingHandler();
        var provider = new WiretappedProvider(new VentaDomainResolver(), handler);

        await MessagingOutboundHandlers.Handle(message, provider, _userService, _db, _bus, default);

        Assert.That(handler.Bodies, Has.Count.EqualTo(1));
        var signed = SignedFederationEvent.Parse(handler.Bodies[0]);
        Assert.That(signed.Payload, Is.InstanceOf<MessageCreated>());
        return (MessageCreated)signed.Payload;
    }

    private async Task<FederatedMessageCreatedReceived> DispatchInboundAsync(MessageCreated wire)
    {
        var opts = new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase($"persona-inbound-{Guid.NewGuid()}")
            .Options;
        await using var remoteDb = new MicroserviceContext(opts);
        remoteDb.FederationInstances.Add(new FederationInstance
        {
            Id = "fein_home", Host = HomeHost, Name = HomeHost, Status = FederationStatus.Active,
        });
        await remoteDb.SaveChangesAsync();

        var remoteBus = new FakeMessageBus();
        await InboundEventDispatcher.DispatchAsync(wire, remoteDb, remoteBus, default);

        Assert.That(remoteBus.Published, Has.Count.EqualTo(1));
        return (FederatedMessageCreatedReceived)remoteBus.Published[0];
    }

    private static (byte[] privateKey, byte[] publicKey) GenerateKeyPair()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return (key.Export(KeyBlobFormat.PkixPrivateKeyText), key.PublicKey.Export(KeyBlobFormat.PkixPublicKeyText));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<byte[]> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class WiretappedProvider : VentaFederationProvider
    {
        private readonly HttpMessageHandler _handler;

        public WiretappedProvider(IFederatedDomainResolver resolver, HttpMessageHandler handler)
            : base(resolver) => _handler = handler;

        protected override HttpClient CreateHttpClient(Uri domain) =>
            new(_handler, disposeHandler: false) { BaseAddress = domain };
    }
}
