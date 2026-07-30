using System.Security.Claims;
using Bots.Application.Endpoints.Discord;
using Bots.Tests.Helpers;
using Guild.Contracts;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class DiscordChannelEndpointTests
{
    private FakeMessagingBus _bus = null!;
    private DiscordChannelEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessagingBus();
        _endpoint = new DiscordChannelEndpoint();
    }

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static object GetValue(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    [Test]
    public async Task GetChannel_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _endpoint.GetChannelAsync("ch_1", anonymous, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetChannel_UnknownChannel_ReturnsNotFound()
    {
        _bus.ChannelResponse = new GetChannelResponse { Channel = null };

        var result = await _endpoint.GetChannelAsync("ch_missing", MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetChannel_NoViewPermission_ReturnsForbid()
    {
        _bus.ChannelResponse = new GetChannelResponse { Channel = new ChannelInfo { Id = "ch_1", GuildId = "gld_1", Name = "general", Type = "Text", Position = 0 } };
        _bus.PermissionResponse = new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = ExternalPermission.ViewChannel };

        var result = await _endpoint.GetChannelAsync("ch_1", MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetChannel_Allowed_ReturnsDiscordShapedChannel()
    {
        _bus.ChannelResponse = new GetChannelResponse
        {
            Channel = new ChannelInfo { Id = "ch_1", GuildId = "gld_1", Name = "general", Type = "Text", Position = 2, CategoryId = "ch_cat" },
        };
        _bus.PermissionResponse = new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = ExternalPermission.ViewChannel };

        var result = await _endpoint.GetChannelAsync("ch_1", MakeUser("usr_bot1"), _bus);

        var value = GetValue(result);
        var id = (string)value.GetType().GetProperty("id")!.GetValue(value)!;
        var guildId = (string)value.GetType().GetProperty("guild_id")!.GetValue(value)!;
        var name = (string)value.GetType().GetProperty("name")!.GetValue(value)!;
        var parentId = (string?)value.GetType().GetProperty("parent_id")!.GetValue(value);
        Assert.Multiple(() =>
        {
            Assert.That(id, Is.EqualTo("ch_1"));
            Assert.That(guildId, Is.EqualTo("gld_1"));
            Assert.That(name, Is.EqualTo("general"));
            Assert.That(parentId, Is.EqualTo("ch_cat"));
        });
    }
}
