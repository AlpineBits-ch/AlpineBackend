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

    [Test]
    public async Task AuthenticateAsync_WrongSecretForACachedBot_DoesNotServeTheCachedJwt()
    {
        // The bot's client id is its BotUserId, which is public. When the cache was keyed on the
        // client id alone, a warm cache entry meant anyone could authenticate as that bot with an
        // arbitrary secret - the secret was never looked at on a cache hit. The exchange must be
        // re-attempted (and here rejected) for a secret that has not itself been verified.
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.OK, """{"access_token":"real-jwt","expires_in":3600}"""),
            (HttpStatusCode.Unauthorized, ""));
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());

        var legitimate = await translator.AuthenticateAsync(DiscordCompatToken.Pack("user_bot1", "secret1"));
        var forged = await translator.AuthenticateAsync(DiscordCompatToken.Pack("user_bot1", "not-the-secret"));

        Assert.Multiple(() =>
        {
            Assert.That(legitimate.Success, Is.True);
            Assert.That(forged.Success, Is.False, "a wrong secret must never be served the cached JWT");
            Assert.That(forged.Jwt, Is.Null);
            Assert.That(handler.CallCount, Is.EqualTo(2), "the wrong secret must fall through to Identity, not hit the cache");
        });
    }

    [Test]
    public async Task AuthenticateAsync_EmptySecret_IsRejectedWithoutCallingIdentity()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"access_token":"real-jwt","expires_in":3600}""");
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());

        var result = await translator.AuthenticateAsync(DiscordCompatToken.Pack("user_bot1", ""));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(handler.CallCount, Is.Zero);
        });
    }
}
