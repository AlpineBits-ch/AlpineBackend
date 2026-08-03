using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Dto.Response;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class MessageCreatedForBotsHandlerTests
{
    private readonly FakeBotChannelVisibility _visibility = new();
    private TestBotsContext _context = null!;
    private FakeGatewayMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeGatewayMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task InstallBotAsync(string botUserId, string guildId)
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = botUserId, Name = "Test Bot" };
        _context.BotApplications.Add(app);
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = guildId,
            InstalledByUserId = "usr_admin", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Handle_NoInstalledBots_NeverResolvesAuthor()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MessageCreatedForBotsHandler.Handle(
            new MessageCreatedForBots { GuildId = "gld_unlinked", ChannelId = "ch_1", MessageId = "m1", AuthorId = "usr_1", Content = "hi"u8.ToArray(), EncryptionState = MessageEncryptionState.Plain },
            _context, registry, _bus, _visibility);

        Assert.That(_bus.Invoked, Is.Empty);
        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task Handle_PlainMessage_DispatchesWithDecodedContent()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.UserResponse = new GetUserByIdResponse { User = new ApplicationUserDto { Id = "usr_author", UserName = "Author", IsBot = false } };

        await MessageCreatedForBotsHandler.Handle(
            new MessageCreatedForBots { GuildId = "gld_1", ChannelId = "ch_1", MessageId = "m1", AuthorId = "usr_author", Content = "hello world"u8.ToArray(), EncryptionState = MessageEncryptionState.Plain },
            _context, registry, _bus, _visibility);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(botUserId, Is.EqualTo("usr_bot1"));
        Assert.That(eventName, Is.EqualTo("MESSAGE_CREATE"));
        Assert.That(data.GetProperty("content").GetString(), Is.EqualTo("hello world"));
        Assert.That(data.GetProperty("author").GetProperty("username").GetString(), Is.EqualTo("Author"));
    }

    [Test]
    public async Task Handle_EncryptedMessage_DispatchesWithEmptyContent()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.UserResponse = new GetUserByIdResponse { User = new ApplicationUserDto { Id = "usr_author", UserName = "Author" } };

        await MessageCreatedForBotsHandler.Handle(
            new MessageCreatedForBots { GuildId = "gld_1", ChannelId = "ch_1", MessageId = "m1", AuthorId = "usr_author", Content = "secret"u8.ToArray(), EncryptionState = MessageEncryptionState.Encrypted },
            _context, registry, _bus, _visibility);

        var (_, _, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(data.GetProperty("content").GetString(), Is.EqualTo(""));
    }

    [Test]
    public async Task Handle_UnknownAuthor_FallsBackToAuthorIdAsUsername()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.UserResponse = new GetUserByIdResponse { User = null };

        await MessageCreatedForBotsHandler.Handle(
            new MessageCreatedForBots { GuildId = "gld_1", ChannelId = "ch_1", MessageId = "m1", AuthorId = "usr_ghost", Content = "hi"u8.ToArray(), EncryptionState = MessageEncryptionState.Plain },
            _context, registry, _bus, _visibility);

        var (_, _, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(data.GetProperty("author").GetProperty("username").GetString(), Is.EqualTo("usr_ghost"));
    }
}
