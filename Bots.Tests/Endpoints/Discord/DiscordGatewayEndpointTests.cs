using AppEnvironment;
using Bots.Application.Endpoints.Discord;
using Microsoft.AspNetCore.Http;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class DiscordGatewayEndpointTests
{
    private string _originalInstanceUrl = null!;

    [SetUp]
    public void SetUp() => _originalInstanceUrl = Env.GeneralConfiguration.InstanceUrl;

    [TearDown]
    public void TearDown() => Env.GeneralConfiguration.InstanceUrl = _originalInstanceUrl;

    // Results.Ok(anonymousObject) returns Ok<TAnonymous>, an anonymous compile-time type this test
    // has no name for - reflection on IResult.Value is the only way to reach it from outside.
    private static object GetValue(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    [Test]
    public void GetGateway_HttpsInstanceUrl_ReturnsWssGatewayUrl()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        var endpoint = new DiscordGatewayEndpoint();

        var value = GetValue(endpoint.GetGateway());

        var url = (string)value.GetType().GetProperty("url")!.GetValue(value)!;
        Assert.That(url, Is.EqualTo("wss://api.venta.gg/api/discord/v10/gateway"));
    }

    [Test]
    public void GetGateway_HttpInstanceUrl_ReturnsWsGatewayUrl()
    {
        Env.GeneralConfiguration.InstanceUrl = "http://localhost:5000";
        var endpoint = new DiscordGatewayEndpoint();

        var value = GetValue(endpoint.GetGateway());

        var url = (string)value.GetType().GetProperty("url")!.GetValue(value)!;
        Assert.That(url, Is.EqualTo("ws://localhost:5000/api/discord/v10/gateway"));
    }

    [Test]
    public void GetGatewayBot_ReturnsUrlAndStaticSessionStartLimit()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        var endpoint = new DiscordGatewayBotEndpoint();

        var value = GetValue(endpoint.GetGatewayBot());

        var url = (string)value.GetType().GetProperty("url")!.GetValue(value)!;
        var shards = (int)value.GetType().GetProperty("shards")!.GetValue(value)!;
        Assert.Multiple(() =>
        {
            Assert.That(url, Is.EqualTo("wss://api.venta.gg/api/discord/v10/gateway"));
            Assert.That(shards, Is.EqualTo(1));
        });
    }
}
