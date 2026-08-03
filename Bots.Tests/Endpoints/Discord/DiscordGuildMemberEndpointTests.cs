using System.Security.Claims;
using Bots.Application.Endpoints.Discord;
using Bots.Tests.Helpers;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class DiscordGuildMemberEndpointTests
{
    private FakeMessagingBus _bus = null!;
    private DiscordGuildMemberEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessagingBus();
        _endpoint = new DiscordGuildMemberEndpoint();
    }

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static object GetValue(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    [Test]
    public async Task ListMembers_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _endpoint.ListMembersAsync("gld_1", null, anonymous, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ListMembers_ReturnsDiscordShapedMembers()
    {
        _bus.MembersResponse = new ListGuildMembersResponse
        {
            Members =
            [
                new GuildMemberSummary { UserId = "usr_1", Nickname = "Nicky", RoleIds = ["rol_1"], JoinedAt = new DateTime(2026, 1, 1), IsBot = false },
                new GuildMemberSummary { UserId = "usr_2", Nickname = null, IsBot = true, JoinedAt = new DateTime(2026, 2, 1) },
            ],
        };

        var result = await _endpoint.ListMembersAsync("gld_1", null, MakeUser("usr_bot1"), _bus);

        var value = GetValue(result);
        var members = ((System.Collections.IEnumerable)value).Cast<object>().ToList();
        Assert.That(members, Has.Count.EqualTo(2));

        var first = members[0];
        var nick = (string?)first.GetType().GetProperty("nick")!.GetValue(first);
        Assert.That(nick, Is.EqualTo("Nicky"));

        var second = members[1];
        var user = second.GetType().GetProperty("user")!.GetValue(second)!;
        var username = (string)user.GetType().GetProperty("username")!.GetValue(user)!;
        // Nickname is null for usr_2 - the endpoint falls back to the raw user id as username.
        Assert.That(username, Is.EqualTo("usr_2"));
    }

    [Test]
    public async Task ListMembers_CallerCannotViewTheGuild_IsForbiddenAndNeverQueriesMembers()
    {
        // The caller's identity used to be read and then discarded, so this returned the full
        // roster of any guild on the instance - and these routes accept an ordinary user JWT, not
        // just a bot token, so every registered account could enumerate every guild's membership.
        _bus.GuildPermissionResponse = new HasUserPermissionToGuildResponse
        {
            IsAllowed = false, Permission = ExternalPermission.ViewChannel,
        };

        var result = await _endpoint.ListMembersAsync("gld_someone_elses", null, MakeUser("usr_outsider"), _bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>());
            Assert.That(_bus.Invoked.OfType<ListGuildMembersRequest>(), Is.Empty,
                "the roster must never be fetched for a caller who cannot view the guild");
        });
    }

    [Test]
    public async Task ListMembers_NoLimitProvided_DefaultsTo100()
    {
        await _endpoint.ListMembersAsync("gld_1", null, MakeUser("usr_bot1"), _bus);

        // OfType rather than Single: the endpoint now also issues a guild permission check first.
        var request = _bus.Invoked.OfType<ListGuildMembersRequest>().Single();
        Assert.That(request.Limit, Is.EqualTo(100));
    }

    [Test]
    public async Task ListMembers_LimitProvided_IsPassedThrough()
    {
        await _endpoint.ListMembersAsync("gld_1", 25, MakeUser("usr_bot1"), _bus);

        // OfType rather than Single: the endpoint now also issues a guild permission check first.
        var request = _bus.Invoked.OfType<ListGuildMembersRequest>().Single();
        Assert.That(request.Limit, Is.EqualTo(25));
    }
}
