using System.Net;
using System.Net.Http.Json;
using Import.Application.Discord;
using Import.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Import.Tests.Discord;

[TestFixture]
public class DiscordApiClientTests
{
    private QueuedHttpMessageHandler _handler = null!;
    private DiscordApiClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new QueuedHttpMessageHandler();
        var factory = new FakeHttpClientFactory(_handler);
        _client = new DiscordApiClient(factory, NullLogger<DiscordApiClient>.Instance);
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    [Test]
    public async Task GetGuildAsync_SuccessResponse_ReturnsDeserializedPayload()
    {
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new DiscordGuildPayload { Id = "g1", Name = "My Server" })
        });

        var result = await _client.GetGuildAsync("g1");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo("g1"));
        Assert.That(result.Name, Is.EqualTo("My Server"));
        Assert.That(_handler.Requests[0].RequestUri!.PathAndQuery, Does.EndWith("/guilds/g1"));
    }

    [Test]
    public async Task GetGuildAsync_NotFound_ReturnsNull()
    {
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await _client.GetGuildAsync("missing-guild");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetGuildRolesAsync_SuccessResponse_ReturnsRoles()
    {
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new List<DiscordRolePayload>
            {
                new() { Id = "r1", Name = "Admin", Permissions = "8" },
            })
        });

        var result = await _client.GetGuildRolesAsync("g1");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Admin"));
    }

    [Test]
    public void GetGuildRolesAsync_ServerError_ThrowsAfterRetriesExhausted()
    {
        // 500 is a transient-error match for the Polly policy, so this exercises the retry path
        // (3 attempts, short backoff) before finally surfacing via EnsureSuccessStatusCode.
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.ThrowsAsync<HttpRequestException>(() => _client.GetGuildRolesAsync("g1"));
    }

    [Test]
    public async Task GetGuildChannelsAsync_SuccessResponse_ReturnsChannels()
    {
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new List<DiscordChannelPayload>
            {
                new() { Id = "c1", Name = "general", Type = 0 },
            })
        });

        var result = await _client.GetGuildChannelsAsync("g1");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo("c1"));
    }

    [Test]
    public async Task LeaveGuildAsync_SuccessResponse_DoesNotThrow()
    {
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.DoesNotThrowAsync(() => _client.LeaveGuildAsync("g1"));
        Assert.That(_handler.Requests[0].Method, Is.EqualTo(HttpMethod.Delete));
    }

    [Test]
    public void LeaveGuildAsync_ServerError_Throws()
    {
        _handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.ThrowsAsync<HttpRequestException>(() => _client.LeaveGuildAsync("g1"));
    }
}
