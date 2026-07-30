using System.Net;
using Bots.Application.Gateway;
using Bots.Application.Middleware;
using Bots.Tests.Helpers;
using Microsoft.AspNetCore.Http;

namespace Bots.Tests.Middleware;

/// <summary>
/// Covers the request pipeline shim in front of BotTokenTranslator (already covered on its own
/// in Gateway/BotTokenTranslatorTests.cs): rewriting a `Bot ...` compat Authorization header into
/// a real Bearer JWT before UseAuthentication() ever sees the request, or passing non-bot
/// requests straight through untouched.
/// </summary>
[TestFixture]
public class DiscordBotTokenTranslationMiddlewareTests
{
    [Test]
    public async Task InvokeAsync_NoAuthorizationHeader_CallsNextUnchanged()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new DiscordBotTokenTranslationMiddleware(next);
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}")), new FakeDistributedCache());
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, translator);

        Assert.Multiple(() =>
        {
            Assert.That(nextCalled, Is.True);
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        });
    }

    [Test]
    public async Task InvokeAsync_NonBotAuthorizationHeader_PassesThroughUnchanged()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new DiscordBotTokenTranslationMiddleware(next);
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}")), new FakeDistributedCache());
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer some-jwt-already";

        await middleware.InvokeAsync(context, translator);

        Assert.Multiple(() =>
        {
            Assert.That(nextCalled, Is.True);
            Assert.That(context.Request.Headers.Authorization.ToString(), Is.EqualTo("Bearer some-jwt-already"));
        });
    }

    [Test]
    public async Task InvokeAsync_ValidBotToken_RewritesAuthorizationToBearerAndCallsNext()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new DiscordBotTokenTranslationMiddleware(next);
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"access_token":"real-jwt","expires_in":3600}""");
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());
        var context = new DefaultHttpContext();
        var packed = DiscordCompatToken.Pack("usr_bot1", "secret1");
        context.Request.Headers.Authorization = $"Bot {packed}";

        await middleware.InvokeAsync(context, translator);

        Assert.Multiple(() =>
        {
            Assert.That(nextCalled, Is.True);
            Assert.That(context.Request.Headers.Authorization.ToString(), Is.EqualTo("Bearer real-jwt"));
        });
    }

    [Test]
    public async Task InvokeAsync_InvalidBotToken_ReturnsUnauthorizedAndDoesNotCallNext()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new DiscordBotTokenTranslationMiddleware(next);
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}")), new FakeDistributedCache());
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bot not-a-valid-packed-token";

        await middleware.InvokeAsync(context, translator);

        Assert.Multiple(() =>
        {
            Assert.That(nextCalled, Is.False);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        });
    }

    [Test]
    public async Task InvokeAsync_IdentityRejectsExchange_ReturnsUnauthorizedAndDoesNotCallNext()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new DiscordBotTokenTranslationMiddleware(next);
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "");
        var translator = new BotTokenTranslator(new FakeHttpClientFactory(handler), new FakeDistributedCache());
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bot {DiscordCompatToken.Pack("usr_bot1", "wrong-secret")}";

        await middleware.InvokeAsync(context, translator);

        Assert.Multiple(() =>
        {
            Assert.That(nextCalled, Is.False);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        });
    }
}
