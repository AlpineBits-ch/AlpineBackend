using Messaging.Application.Handler.Messages;
using Messaging.Contracts.Bus.Request;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>What Guild rewrites a channel's denormalized head to once the message it named is gone.</summary>
[TestFixture]
public class GetChannelHeadHandlerTests
{
    private const string ChannelId = "chnl-1";

    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Message> SeedAsync(string channelId, DateTimeOffset createdAt)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "hello"u8.ToArray(),
            ChannelId = channelId,
            AuthorId = "user-author",
        });

        message.CreatedAt = createdAt;

        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();
        return message;
    }

    private Task<Contracts.Bus.Response.GetChannelHeadResponse> InvokeAsync(string channelId = ChannelId) =>
        GetChannelHeadHandler.Handle(
            new GetChannelHeadRequest { ChannelId = channelId },
            _repo,
            new RecordingLogger<GetChannelHeadHandler>());

    [Test]
    public async Task Handle_AnswersTheNewestMessage()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(ChannelId, now.AddMinutes(-10));
        var newest = await SeedAsync(ChannelId, now.AddMinutes(-1));

        var response = await InvokeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.MessageId, Is.EqualTo(newest.Id));
            Assert.That(response.CreatedAt, Is.EqualTo(newest.CreatedAt));
        });
    }

    [Test]
    public async Task Handle_IgnoresOtherChannels()
    {
        var now = DateTimeOffset.UtcNow;
        var mine = await SeedAsync(ChannelId, now.AddMinutes(-10));
        await SeedAsync("chnl-elsewhere", now);

        var response = await InvokeAsync();

        Assert.That(response.MessageId, Is.EqualTo(mine.Id));
    }

    [Test]
    public async Task Handle_EmptyChannel_AnswersNothing()
    {
        var response = await InvokeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.MessageId, Is.Null);
            Assert.That(response.CreatedAt, Is.Null);
        });
    }
}
