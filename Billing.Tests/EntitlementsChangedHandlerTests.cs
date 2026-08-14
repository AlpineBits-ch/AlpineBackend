using Billing.Application.Bus.Consumers;
using Billing.Contracts.Bus.Events;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Echo.Entitlements.Wire;
using Echo.Realtime;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Billing.Tests;

/// <summary>Routing of the <c>entitlements.Changed</c> push.</summary>
[TestFixture]
public class EntitlementsChangedHandlerTests
{
    private IHubContext<EchoRealtimeHub> _hub = null!;
    private IHubClients _clients = null!;
    private IClientProxy _proxy = null!;
    private IMessageBus _bus = null!;

    [SetUp]
    public void Setup()
    {
        _proxy = Substitute.For<IClientProxy>();
        _clients = Substitute.For<IHubClients>();
        _clients.User(Arg.Any<string>()).Returns(_proxy);
        _clients.Users(Arg.Any<IReadOnlyList<string>>()).Returns(_proxy);

        _hub = Substitute.For<IHubContext<EchoRealtimeHub>>();
        _hub.Clients.Returns(_clients);

        _bus = Substitute.For<IMessageBus>();
    }

    private Task HandleAsync(EntitlementsChanged message) =>
        EntitlementsChangedHandler.Handle(
            message, _hub, _bus, NullLogger<EntitlementsChangedHandler>.Instance, CancellationToken.None);

    private static EntitlementsChanged Change(SubjectKind kind, string id) => new()
    {
        SubjectKind = kind,
        SubjectId = id,
        Reason = EntitlementsChangedReason.GrantIssued,
        Version = 7,
        ChangedKeys = ["guild.emoji_slots"],
        OccurredAt = DateTimeOffset.UtcNow,
    };

    /// <summary>The same envelope the gateway's own notifier sends, under the same event name. The
    /// wire contract lives in the shared library so these two cannot drift; this asserts that this
    /// producer actually uses it.</summary>
    [Test]
    public async Task A_user_change_goes_to_that_user_alone_as_the_shared_envelope()
    {
        await HandleAsync(Change(SubjectKind.User, Subjects.User.Id));

        _clients.Received(1).User(Subjects.User.Id);
        _clients.DidNotReceive().Users(Arg.Any<IReadOnlyList<string>>());
        await _bus.DidNotReceive().InvokeAsync<ListGuildMembersResponse>(Arg.Any<object>());

        await _proxy.Received(1).SendCoreAsync(
            EntitlementRealtimeEvents.Changed,
            Arg.Is<object?[]>(args => Payload(args)!.SubjectId == Subjects.User.Id
                                      && Payload(args)!.SubjectKind == EntitlementSubjectKinds.User
                                      && Payload(args)!.Version == 7),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Membership belongs to Guild and Billing holds none of it, so the recipient list is
    /// asked for rather than guessed.</summary>
    [Test]
    public async Task A_guild_change_goes_to_the_members_guild_reports()
    {
        _bus.InvokeAsync<ListGuildMembersResponse>(Arg.Any<object>()).Returns(new ListGuildMembersResponse
        {
            Members =
            [
                new GuildMemberSummary { UserId = "user_a" },
                new GuildMemberSummary { UserId = "user_b" },
                new GuildMemberSummary { UserId = "bot_c", IsBot = true },
            ],
        });

        await HandleAsync(Change(SubjectKind.Guild, Subjects.Guild.Id));

        _clients.Received(1).Users(Arg.Is<IReadOnlyList<string>>(
            ids => ids != null && ids.Count == 2 && ids.Contains("user_a") && ids.Contains("user_b")));

        await _proxy.Received(1).SendCoreAsync(
            EntitlementRealtimeEvents.Changed,
            Arg.Is<object?[]>(args => Payload(args)!.SubjectKind == EntitlementSubjectKinds.Guild),
            Arg.Any<CancellationToken>());
    }

    /// <summary>An empty answer is a no-op rather than a broadcast.</summary>
    [Test]
    public async Task A_guild_with_no_members_pushes_nothing()
    {
        _bus.InvokeAsync<ListGuildMembersResponse>(Arg.Any<object>())
            .Returns(new ListGuildMembersResponse());

        await HandleAsync(Change(SubjectKind.Guild, Subjects.Guild.Id));

        await _proxy.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Guild being unreachable must not fail the handler.</summary>
    [Test]
    public void A_membership_lookup_failure_does_not_fail_the_handler()
    {
        _bus.InvokeAsync<ListGuildMembersResponse>(Arg.Any<object>())
            .Returns<Task<ListGuildMembersResponse>>(_ => throw new TimeoutException("Guild is down."));

        Assert.That(async () => await HandleAsync(Change(SubjectKind.Guild, Subjects.Guild.Id)),
            Throws.Nothing);
    }

    /// <summary>The single argument SendCoreAsync carries.</summary>
    private static EntitlementsChangedDto? Payload(object?[]? args) =>
        args is [EntitlementsChangedDto dto, ..] ? dto : null;
}
