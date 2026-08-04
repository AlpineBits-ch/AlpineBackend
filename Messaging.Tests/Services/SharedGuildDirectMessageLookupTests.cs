using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Services.Privacy;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Services;

/// <summary>
/// T2-14 as consumed by T0-2's <c>FriendsAndServerMembers</c> branch: take the pairwise guild
/// intersection first, then ask the recipient's per-guild DM preference about exactly that set.
/// </summary>
[TestFixture]
public class SharedGuildDirectMessageLookupTests
{
    private const string Recipient = "user-2";
    private const string Initiator = "user-1";

    private static SharedGuildDirectMessageLookup Lookup(FakeMessageBus bus) =>
        new(bus, NullLogger<SharedGuildDirectMessageLookup>.Instance);

    /// <summary>
    /// Guild, as far as this lookup can see it: a pairwise intersection, and an effective
    /// preference per guild for the recipient.
    /// </summary>
    private static FakeMessageBus Bus(
        IReadOnlyCollection<string> sharedGuildIds,
        IDictionary<string, bool>? recipientAllows = null) =>
        new(msg => msg switch
        {
            GetSharedGuildsRequest => new GetSharedGuildsResponse
            {
                Shared = sharedGuildIds.Count == 0
                    ? []
                    : [new SharedGuildsSummary { OtherUserId = Recipient, GuildIds = sharedGuildIds.ToList() }],
            },

            GetGuildDirectMessagePreferenceRequest r => new GetGuildDirectMessagePreferenceResponse
            {
                Preferences = r.GuildIds
                    .Select(g => new GuildDirectMessagePreferenceSummary
                    {
                        GuildId = g,
                        AllowDirectMessages = recipientAllows?.TryGetValue(g, out var allow) == true && allow,
                    })
                    .ToList(),
            },

            _ => throw new InvalidOperationException("unexpected"),
        });

    [Test]
    public async Task ASharedGuildTheRecipientAcceptsDmsFrom_Admits()
    {
        var bus = Bus(["guild-1"], new Dictionary<string, bool> { ["guild-1"] = true });

        Assert.That(await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator), Is.True);
    }

    [Test]
    public async Task ASharedGuildTheRecipientTurnedDmsOffFor_DoesNotAdmit()
    {
        // The T2-14 control doing its job: they are in the same server and it still refuses.
        var bus = Bus(["guild-1"], new Dictionary<string, bool> { ["guild-1"] = false });

        Assert.That(await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator), Is.False);
    }

    [Test]
    public async Task NoSharedGuildAtAll_DoesNotAdmit()
    {
        var bus = Bus([]);

        Assert.That(await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator), Is.False);
    }

    [Test]
    public async Task OneAcceptingSharedGuildAmongSeveralIsEnough()
    {
        var bus = Bus(
            ["guild-1", "guild-2", "guild-3"],
            new Dictionary<string, bool> { ["guild-1"] = false, ["guild-2"] = true, ["guild-3"] = false });

        Assert.That(await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator), Is.True);
    }

    [Test]
    public async Task WithNoSharedGuild_ThePreferenceIsNeverAskedFor()
    {
        var bus = Bus([]);

        await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator);

        Assert.That(bus.Invoked.OfType<GetGuildDirectMessagePreferenceRequest>(), Is.Empty);
    }

    [Test]
    public async Task ThePreferenceIsAskedOfTheRecipient_ScopedToTheSharedGuildsOnly()
    {
        // Both halves matter.
        var bus = Bus(["guild-1", "guild-2"], new Dictionary<string, bool> { ["guild-1"] = true });

        await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator);

        var preference = bus.Invoked.OfType<GetGuildDirectMessagePreferenceRequest>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(preference.UserId, Is.EqualTo(Recipient));
            Assert.That(preference.GuildIds, Is.EquivalentTo(new[] { "guild-1", "guild-2" }));
            Assert.That(preference.GuildIds, Is.Not.Empty,
                "An empty list means 'every guild you are in', which is the enumeration this ordering exists to avoid");
        });
    }

    [Test]
    public async Task TheIntersectionIsTakenFromTheInitiator()
    {
        var bus = Bus(["guild-1"], new Dictionary<string, bool> { ["guild-1"] = true });

        await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator);

        var shared = bus.Invoked.OfType<GetSharedGuildsRequest>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(shared.UserId, Is.EqualTo(Initiator));
            Assert.That(shared.OtherUserIds, Is.EquivalentTo(new[] { Recipient }));
        });
    }

    [Test]
    public async Task AUserIsNeverIntersectedWithThemselves()
    {
        // That request is ignored by contract, and its answer would be the caller's own complete
        // guild list - so it is not made at all.
        var bus = Bus(["guild-1"], new Dictionary<string, bool> { ["guild-1"] = true });

        var admitted = await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Initiator, Initiator);

        Assert.Multiple(() =>
        {
            Assert.That(admitted, Is.False);
            Assert.That(bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task AnUnreachableGuildServiceFailsClosed()
    {
        var bus = new FakeMessageBus(_ => throw new InvalidOperationException("guild is down"));

        Assert.That(await Lookup(bus).SharesDirectMessageEnabledGuildAsync(Recipient, Initiator), Is.False);
    }
}
