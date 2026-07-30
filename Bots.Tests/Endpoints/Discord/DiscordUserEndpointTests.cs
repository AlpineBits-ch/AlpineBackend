using System.Security.Claims;
using Bots.Application.Endpoints.Discord;
using Bots.Tests.Helpers;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Dto.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class DiscordUserEndpointTests
{
    private FakeMessagingBus _bus = null!;
    private DiscordUserEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessagingBus();
        _endpoint = new DiscordUserEndpoint();
    }

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    // Results.Ok(anonymousObject) returns Ok<TAnonymous>, an anonymous compile-time type this test
    // has no name for - reflection on IResult.Value is the only way to reach it from outside.
    private static object GetValue(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    [Test]
    public async Task GetSelf_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _endpoint.GetSelfAsync(anonymous, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetSelf_UnknownUser_ReturnsNotFound()
    {
        _bus.UserResponse = new GetUserByIdResponse { User = null };

        var result = await _endpoint.GetSelfAsync(MakeUser("usr_bot1"), _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetSelf_KnownUser_ReturnsSelfAsBot()
    {
        _bus.UserResponse = new GetUserByIdResponse { User = new ApplicationUserDto { Id = "usr_bot1", UserName = "MyBot" } };

        var result = await _endpoint.GetSelfAsync(MakeUser("usr_bot1"), _bus);

        var value = GetValue(result);
        var id = (string)value.GetType().GetProperty("id")!.GetValue(value)!;
        var username = (string)value.GetType().GetProperty("username")!.GetValue(value)!;
        var bot = (bool)value.GetType().GetProperty("bot")!.GetValue(value)!;
        Assert.Multiple(() =>
        {
            Assert.That(id, Is.EqualTo("usr_bot1"));
            Assert.That(username, Is.EqualTo("MyBot"));
            Assert.That(bot, Is.True);
        });
    }
}
