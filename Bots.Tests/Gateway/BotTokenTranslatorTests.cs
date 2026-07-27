using System.Net;
using Bots.Application.Gateway;
using Bots.Application.Middleware;
using Bots.Tests.Helpers;

namespace Bots.Tests.Gateway;

/// <summary>
/// Covers BotTokenTranslator, extracted out of DiscordBotTokenTranslationMiddleware so the same
/// unpack -> cache -> /connect/token exchange logic is shared with the Gateway WebSocket's
/// IDENTIFY handler.
/// </summary>
[TestFixture]
public class BotTokenTranslatorTests
{
    [Test]
    public async Task AuthenticateAsync_ValidTokenAndSuccessfulExchange_ReturnsSuccessWithBotUserIdAndJwt()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"access_token":"real-jwt","expires_in":3600}""");
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());
        var packed = DiscordCompatToken.Pack("user_bot1", "secret1");

        var result = await translator.AuthenticateAsync(packed);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.BotUserId, Is.EqualTo("user_bot1"));
            Assert.That(result.Jwt, Is.EqualTo("real-jwt"));
        });
    }

    [Test]
    public async Task AuthenticateAsync_NotValidCompatToken_ReturnsFailed()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"access_token":"real-jwt","expires_in":3600}""");
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());

        var result = await translator.AuthenticateAsync("not-a-valid-packed-token!!!");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(handler.CallCount, Is.Zero, "should never call Identity for a token that doesn't even unpack");
        });
    }

    [Test]
    public async Task AuthenticateAsync_IdentityRejectsExchange_ReturnsFailed()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "");
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());
        var packed = DiscordCompatToken.Pack("user_bot1", "wrong-secret");

        var result = await translator.AuthenticateAsync(packed);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task AuthenticateAsync_SecondCallForSameBot_UsesCachedJwtInsteadOfExchangingAgain()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"access_token":"real-jwt","expires_in":3600}""");
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());
        var packed = DiscordCompatToken.Pack("user_bot1", "secret1");

        await translator.AuthenticateAsync(packed);
        await translator.AuthenticateAsync(packed);

        Assert.That(handler.CallCount, Is.EqualTo(1));
    }
}
