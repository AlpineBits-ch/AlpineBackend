using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Services;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Services;

/// <summary>
/// Which number "how long may this message be" resolves to: the guild's plan in a channel, the
/// sender's own plan in a DM, and the instance ceiling above and behind both of them.
/// </summary>
[TestFixture]
public class MessageLengthPolicyTests
{
    private const string ChannelId = "chan-1";
    private const string GuildId = "guild-1";
    private const string UserId = "user-1";

    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp() => _cache = new FakeDistributedCache();

    // ══════════════════════════════════════════════════════════════════════════ unconfigured

    /// <summary>
    /// The normal state of a self-hosted instance, and the shape that once made account credit
    /// unspendable: nothing configured must not resolve to nothing allowed.
    /// </summary>
    [Test]
    public async Task No_resolver_at_all_lands_on_the_hard_ceiling()
    {
        var limit = await Policy(ChannelBus()).ForAsync(ChannelId, null, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(limit.MaxCharacters, Is.EqualTo(MessageLengthPolicy.HardCeilingCharacters));
            Assert.That(limit.MaxCharacters, Is.GreaterThan(4000),
                "an unconfigured instance must not land below what the free tier would have given");
        });
    }

    [Test]
    public async Task An_empty_catalogue_lands_on_the_hard_ceiling_too()
    {
        var policy = Policy(ChannelBus(), Resolver(EntitlementSet.Empty, EntitlementSet.Empty));

        var limit = await policy.ForAsync(ChannelId, null, UserId);

        Assert.That(limit.MaxCharacters, Is.EqualTo(MessageLengthPolicy.HardCeilingCharacters));
    }

    [Test]
    public async Task An_empty_catalogue_lands_on_the_hard_ceiling_in_a_dm_as_well()
    {
        var policy = Policy(ChannelBus(), Resolver(EntitlementSet.Empty, EntitlementSet.Empty));

        var limit = await policy.ForAsync(null, "conv-1", UserId);

        Assert.That(limit.MaxCharacters, Is.EqualTo(MessageLengthPolicy.HardCeilingCharacters));
    }

    // ══════════════════════════════════════════════════════════════════════════ plans

    [Test]
    public async Task A_channel_resolves_the_guild_key_and_its_plan_number()
    {
        var policy = Policy(ChannelBus(), Resolver(GuildSet(4000), EntitlementSet.Empty));

        var limit = await policy.ForAsync(ChannelId, null, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(limit.MaxCharacters, Is.EqualTo(4000));
            Assert.That(limit.Key.Name, Is.EqualTo("guild.message_max_length"));
            Assert.That(limit.Subject.Id, Is.EqualTo(GuildId));
            Assert.That(limit.Reason, Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit));
        });
    }

    /// <summary>A DM has no guild, so the guild key must not be the one that answers.</summary>
    [Test]
    public async Task A_conversation_resolves_the_user_key()
    {
        var policy = Policy(ChannelBus(), Resolver(GuildSet(4000), UserSet(15_000)));

        var limit = await policy.ForAsync(null, "conv-1", UserId);

        Assert.Multiple(() =>
        {
            Assert.That(limit.MaxCharacters, Is.EqualTo(15_000));
            Assert.That(limit.Key.Name, Is.EqualTo("user.message_max_length"));
            Assert.That(limit.Subject.Id, Is.EqualTo(UserId));
            Assert.That(limit.Reason, Is.EqualTo(EntitlementDegradationReason.UserPlanLimit));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ the hard cap

    /// <summary>
    /// The cap is absolute: a catalogue edited to something larger, by mistake or by an operator who
    /// thought it was theirs to set, does not raise it.
    /// </summary>
    [Test]
    public async Task A_plan_above_the_hard_cap_is_clamped_to_it()
    {
        var policy = Policy(ChannelBus(), Resolver(GuildSet(1_000_000), EntitlementSet.Empty));

        var limit = await policy.ForAsync(ChannelId, null, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(limit.MaxCharacters, Is.EqualTo(MessageLengthPolicy.HardCeilingCharacters));
            Assert.That(limit.Reason, Is.EqualTo(EntitlementDegradationReason.OperatorCeiling),
                "the instance bound, not the plan, so no upgrade would lift it");
        });
    }

    [Test]
    public async Task An_operator_ceiling_below_the_plan_binds_and_is_reported_as_such()
    {
        var ceilings = OperatorCeilings.Parse(new Dictionary<string, string?>
        {
            ["guild.message_max_length"] = "1500",
        });

        var policy = Policy(ChannelBus(), Resolver(GuildSet(15_000), EntitlementSet.Empty), ceilings);

        var limit = await policy.ForAsync(ChannelId, null, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(limit.MaxCharacters, Is.EqualTo(1500));
            Assert.That(limit.Reason, Is.EqualTo(EntitlementDegradationReason.OperatorCeiling));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ failure modes

    /// <summary>
    /// Refusing a long post because an unrelated service is unreachable would turn a soft limit into
    /// an availability dependency on the hottest path in the product.
    /// </summary>
    [Test]
    public async Task An_unresolvable_guild_falls_open_to_the_hard_ceiling()
    {
        var bus = new FakeMessageBus(_ => throw new InvalidOperationException("guild unreachable"));
        var policy = Policy(bus, Resolver(GuildSet(4000), EntitlementSet.Empty));

        var limit = await policy.ForAsync(ChannelId, null, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(limit.MaxCharacters, Is.EqualTo(MessageLengthPolicy.HardCeilingCharacters));
            Assert.That(limit.Reason, Is.EqualTo(EntitlementDegradationReason.OperatorCeiling));
        });
    }

    /// <summary>The send path asks on every post, so the answer must not cost a round trip twice.</summary>
    [Test]
    public async Task The_channel_to_guild_answer_is_cached()
    {
        var bus = ChannelBus();
        var policy = Policy(bus, Resolver(GuildSet(4000), EntitlementSet.Empty));

        await policy.ForAsync(ChannelId, null, UserId);
        await policy.ForAsync(ChannelId, null, UserId);

        Assert.That(bus.Invoked.OfType<GetChannelRequest>().Count(), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════ the limit itself

    [Test]
    public void Exactly_at_the_limit_fits_and_one_over_it_does_not()
    {
        var limit = new MessageLengthLimit(
            4000, EntitlementKeys.GuildMessageMaxLength, EntitlementSubject.ForGuild(GuildId),
            EntitlementDegradationReason.GuildPlanLimit);

        Assert.Multiple(() =>
        {
            Assert.That(limit.Exceeds(4000), Is.False);
            Assert.That(limit.Exceeds(4001), Is.True);
        });
    }

    /// <summary>
    /// The server cannot count the characters in something it cannot read, so ciphertext is measured
    /// against an envelope allowance instead of being refused for being longer than its plaintext.
    /// </summary>
    [Test]
    public void Ciphertext_gets_an_allowance_over_the_plain_ceiling()
    {
        var limit = new MessageLengthLimit(
            4000, EntitlementKeys.GuildMessageMaxLength, EntitlementSubject.ForGuild(GuildId),
            EntitlementDegradationReason.GuildPlanLimit);

        Assert.Multiple(() =>
        {
            Assert.That(limit.Exceeds(8000, encrypted: true), Is.False,
                "armoured ciphertext of a post well inside the ceiling must not be refused");
            Assert.That(limit.Exceeds(8000), Is.True, "the same body in plaintext is over it");
            Assert.That(limit.Exceeds(4000 * MessageLengthLimit.CiphertextAllowance + 1, encrypted: true), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ the refusal

    [Test]
    public async Task The_refusal_carries_the_limit_and_the_length_as_numbers()
    {
        var policy = Policy(ChannelBus(), Resolver(GuildSet(4000), EntitlementSet.Empty));
        var limit = await policy.ForAsync(ChannelId, null, UserId);

        var result = await policy.RefuseAsync(limit, 4180, encrypted: false, UserId);
        var body = ((IValueHttpResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(((IStatusCodeHttpResult)result).StatusCode,
                Is.EqualTo(MessageLengthPolicy.TooLongStatusCode));
            Assert.That(Read(body, "error"), Is.EqualTo(MessageLengthPolicy.TooLongError));
            Assert.That(Read(body, "maxLength"), Is.EqualTo(4000));
            Assert.That(Read(body, "length"), Is.EqualTo(4180),
                "a client renders both numbers, so neither may live only in prose");
        });
    }

    private static object? Read(object body, string property) =>
        body.GetType().GetProperty(property)!.GetValue(body);

    // ══════════════════════════════════════════════════════════════════════════ helpers

    private MessageLengthPolicy Policy(
        FakeMessageBus bus, EntitlementResolver? resolver = null, OperatorCeilings? ceilings = null) =>
        new(bus, _cache, NullLogger<MessageLengthPolicy>.Instance, resolver, ceilings);

    /// <summary>Answers the one question the channel path asks of Guild.</summary>
    private static FakeMessageBus ChannelBus(string guildId = GuildId) => new(msg => msg switch
    {
        GetChannelRequest r => new GetChannelResponse
        {
            Channel = new ChannelInfo { Id = r.ChannelId, GuildId = guildId, Name = "general", Type = "Text" },
        },
        _ => throw new InvalidOperationException("unexpected " + msg.GetType().Name),
    });

    private static EntitlementResolver Resolver(EntitlementSet guild, EntitlementSet user) =>
        new([new StubSource(guild, user)]);

    private static EntitlementSet GuildSet(long maxLength) =>
        new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault)
            .Number(EntitlementKeys.GuildMessageMaxLength, maxLength, "test-plan")
            .Build();

    private static EntitlementSet UserSet(long maxLength) =>
        new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault)
            .Number(EntitlementKeys.UserMessageMaxLength, maxLength, "test-plan")
            .Build();

    private sealed class StubSource(EntitlementSet guild, EntitlementSet user) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.PlanDefault;

        public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult(subject.Kind == SubjectKind.Guild ? guild : user);
    }
}
