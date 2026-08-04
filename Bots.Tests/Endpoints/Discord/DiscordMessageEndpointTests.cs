using System.Security.Claims;
using System.Text;
using System.Text.Json;
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

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordEditMessageDto { Content = "edit" }, anonymous, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Edit_MessageNotFound_ReturnsNotFound()
    {
        _bus.UpdateResponse = new UpdateMessageResponse { NotFound = true };

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_missing", new DiscordEditMessageDto { Content = "edit" }, MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Edit_NotOriginalAuthor_ReturnsForbid()
    {
        _bus.UpdateResponse = new UpdateMessageResponse { Forbidden = true };

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordEditMessageDto { Content = "edit" }, MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Edit_Success_ReturnsUpdatedMessageShape()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        _bus.UpdateResponse = new UpdateMessageResponse
        {
            Success = true, UpdatedAt = updatedAt, Content = "edited"u8.ToArray(),
        };

        var result = await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordEditMessageDto { Content = "edited" }, MakeUser("usr_bot1"), _bus);

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

    // ── Edit: absent vs explicitly-empty (Discord PATCH semantics) ─────────────

    /// <summary>The reported bug: a content-only edit used to send EmbedsJson = null, which the
    /// command wrote straight over the stored embeds.</summary>
    [Test]
    public async Task Edit_ContentOnly_SendsNoEmbedsOrComponentsSoTheyAreLeftAlone()
    {
        await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordEditMessageDto { Content = "edited" }, MakeUser("usr_bot1"), _bus);

        var command = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetString(command.Content!), Is.EqualTo("edited"));
            Assert.That(command.EmbedsJson, Is.Null, "null is the command's 'leave the stored embeds alone'");
            Assert.That(command.ComponentsJson, Is.Null);
        });
    }

    [Test]
    public async Task Edit_ExplicitEmptyEmbedsArray_ClearsThem()
    {
        await _endpoint.EditMessageAsync("ch_1", "msg_1", new DiscordEditMessageDto { Embeds = [] }, MakeUser("usr_bot1"), _bus);

        var command = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.EmbedsJson, Is.EqualTo("[]"), "an explicitly empty array is the clear");
            Assert.That(command.Content, Is.Null, "an embeds-only edit must not rewrite the bot's own text");
        });
    }

    [Test]
    public async Task Edit_WithEmbeds_ReplacesThem()
    {
        var dto = new DiscordEditMessageDto { Embeds = [new EmbedPayload { Title = "New Card" }] };

        await _endpoint.EditMessageAsync("ch_1", "msg_1", dto, MakeUser("usr_bot1"), _bus);

        var command = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.That(command.EmbedsJson, Does.Contain("New Card"));
    }

    /// <summary>The distinction only exists if it survives JSON binding - a DTO whose `embeds`
    /// setter never ran is what "the body did not mention embeds" actually looks like.</summary>
    [Test]
    public void EditDto_BodyWithoutAnEmbedsKey_IsNotTreatedAsAnEmptyEmbedsArray()
    {
        var dto = JsonSerializer.Deserialize<DiscordEditMessageDto>("""{"content":"just text"}""", JsonSerializerOptions.Web)!;

        Assert.Multiple(() =>
        {
            Assert.That(dto.HasContent, Is.True);
            Assert.That(dto.HasEmbeds, Is.False);
            Assert.That(dto.HasComponents, Is.False);
        });
    }

    [Test]
    public void EditDto_BodyWithAnEmptyEmbedsArray_CountsAsPresent()
    {
        var dto = JsonSerializer.Deserialize<DiscordEditMessageDto>("""{"content":"t","embeds":[]}""", JsonSerializerOptions.Web)!;

        Assert.Multiple(() =>
        {
            Assert.That(dto.HasEmbeds, Is.True);
            Assert.That(dto.Embeds, Is.Empty);
        });
    }

    /// <summary>Discord's "explicitly null clears it" - present, so it must reach the command as an
    /// empty array rather than as the command's "leave it alone" null.</summary>
    [Test]
    public async Task EditDto_ExplicitNullEmbeds_ClearsRatherThanLeavesAlone()
    {
        var dto = JsonSerializer.Deserialize<DiscordEditMessageDto>("""{"embeds":null}""", JsonSerializerOptions.Web)!;
        Assert.That(dto.HasEmbeds, Is.True);

        await _endpoint.EditMessageAsync("ch_1", "msg_1", dto, MakeUser("usr_bot1"), _bus);

        var command = _bus.Invoked.OfType<UpdateMessageCommand>().Single();
        Assert.That(command.EmbedsJson, Is.EqualTo("[]"));
    }
}
