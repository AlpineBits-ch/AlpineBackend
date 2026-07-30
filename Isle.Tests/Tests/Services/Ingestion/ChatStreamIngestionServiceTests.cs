using Isle.Api.Services.Ingestion;
using Isle.Contracts.Events.Chat;
using Isle.Tests.Helpers;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Services.Ingestion;

/// <summary>
/// Covers <see cref="ChatStreamIngestionService.PublishAsync"/> - the one piece of real logic on
/// this class, the rest being inherited read-loop plumbing already covered by
/// BridgeStreamIngestionServiceTests. PublishAsync is `protected override`, so it is invoked via
/// <see cref="ProtectedInvoke"/> rather than fighting the sealed class or the full SSE loop.
/// </summary>
[TestFixture]
public class ChatStreamIngestionServiceTests
{
    private ChatStreamIngestionService _service = null!;
    private IMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = Substitute.For<IMessageBus>();
        _service = new ChatStreamIngestionService(
            Substitute.For<IChatStream>(),
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<ChatStreamIngestionService>.Instance);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    [Test]
    public async Task PublishAsync_MapsEveryFieldOntoTheContractEvent()
    {
        var message = new ChatMessage { Steam = "76561", Name = "Rex", Text = "hello world", Mode = 1, Ts = 555 };

        await ProtectedInvoke.InvokeTaskAsync(_service, "PublishAsync", message, _bus, CancellationToken.None);

        await _bus.Received(1).PublishAsync(Arg.Is<ChatMessageReceivedEvent>(e =>
            e.SteamId == "76561" && e.Name == "Rex" && e.Text == "hello world" && e.Mode == 1 && e.TimestampMs == 555));
    }

    [Test]
    public async Task PublishAsync_NameOmittedByTheBridge_PublishesWithNullName()
    {
        var message = new ChatMessage { Steam = "76561", Name = null, Text = "hi", Mode = 0, Ts = 1 };

        await ProtectedInvoke.InvokeTaskAsync(_service, "PublishAsync", message, _bus, CancellationToken.None);

        await _bus.Received(1).PublishAsync(Arg.Is<ChatMessageReceivedEvent>(e => e.Name == null && e.SteamId == "76561"));
    }

    [Test]
    public async Task PublishAsync_PublishesExactlyOncePerMessage()
    {
        var message = new ChatMessage { Steam = "76561", Text = "spam?", Mode = 3, Ts = 2 };

        await ProtectedInvoke.InvokeTaskAsync(_service, "PublishAsync", message, _bus, CancellationToken.None);

        await _bus.Received(1).PublishAsync(Arg.Any<ChatMessageReceivedEvent>());
    }
}
