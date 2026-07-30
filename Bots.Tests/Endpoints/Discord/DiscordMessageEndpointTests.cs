using System.Security.Claims;
using System.Text;
using Bots.Application.Endpoints.Discord;
using Bots.Contracts.Gateway.Payloads;
using Bots.Tests.Helpers;
using Guild.Contracts;
using Guild.Contracts.Bus.Response;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class DiscordMessageEndpointTests
{
    private FakeMessagingBus _bus = null!;
    private DiscordMessageEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessagingBus();
        _endpoint = new DiscordMessageEndpoint();
    }

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static object GetValue(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    // ── CreateMessageAsync ────────────────────────────────────────────────────

    [Test]
    public async Task Create_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _endpoint.CreateMessageAsync("ch_1", new DiscordCreateMessageDto { Content = "hi" }, anonymous, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Create_NoSendMessagesPermission_ReturnsForbid()
    {
        _bus.PermissionResponse = new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = ExternalPermission.SendMessages };

        var result = await _endpoint.CreateMessageAsync("ch_1", new DiscordCreateMessageDto { Content = "hi" }, MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Create_WithContent_PostsCommandWithAuthorIdTypeBot()
    {
        var result = await _endpoint.CreateMessageAsync("ch_1", new DiscordCreateMessageDto { Content = "hello" }, MakeUser("usr_bot1"), _bus);

        var command = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.AuthorId, Is.EqualTo("usr_bot1"));
            Assert.That(command.AuthorIdType, Is.EqualTo(AuthorIdType.Bot));
            Assert.That(Encoding.UTF8.GetString(command.Content), Is.EqualTo("hello"));
        });

        var value = GetValue(result);
        var content = (string)value.GetType().GetProperty("content")!.GetValue(value)!;
        Assert.That(content, Is.EqualTo("hello"));
    }

    [Test]
    public async Task Create_EmptyContentWithEmbeds_FallsBackToFlattenedEmbedText()
    {
        var dto = new DiscordCreateMessageDto { Content = "", Embeds = [new EmbedPayload { Title = "My Embed" }] };

        var result = await _endpoint.CreateMessageAsync("ch_1", dto, MakeUser("usr_bot1"), _bus);

        var command = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(Encoding.UTF8.GetString(command.Content), Does.Contain("My Embed"));
        Assert.That(command.EmbedsJson, Is.Not.Null);

        var value = GetValue(result);
        var content = (string)value.GetType().GetProperty("content")!.GetValue(value)!;
        Assert.That(content, Does.Contain("My Embed"));
    }

    // ── EditMessageAsync ──────────────────────────────────────────────────────

    [Test]
    public async Task Edit_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordCreateMessageDto { Content = "edit" }, anonymous, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Edit_MessageNotFound_ReturnsNotFound()
    {
        _bus.UpdateResponse = new UpdateMessageResponse { NotFound = true };

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_missing", new DiscordCreateMessageDto { Content = "edit" }, MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Edit_NotOriginalAuthor_ReturnsForbid()
    {
        _bus.UpdateResponse = new UpdateMessageResponse { Forbidden = true };

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordCreateMessageDto { Content = "edit" }, MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Edit_Success_ReturnsUpdatedMessageShape()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        _bus.UpdateResponse = new UpdateMessageResponse { Success = true, UpdatedAt = updatedAt };

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordCreateMessageDto { Content = "edited" }, MakeUser("usr_bot1"), _bus);

        var command = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.MessageId, Is.EqualTo("msg_1"));
            Assert.That(command.RequestingAuthorId, Is.EqualTo("usr_bot1"));
        });

        var value = GetValue(result);
        var content = (string)value.GetType().GetProperty("content")!.GetValue(value)!;
        Assert.That(content, Is.EqualTo("edited"));
    }
}
