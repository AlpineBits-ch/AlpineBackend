using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus;

/// <summary>
/// The Guild half of Messaging's internal-link previews: what an invite card is allowed to say
/// about a guild, to everyone who can read the message the link was posted in.
/// </summary>
[TestFixture]
public class ResolveInviteCardHandlerTests
{
    private TestGuildContext _ctx = null!;
    private VanityUrlService _vanity = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new TestGuildContext(Guid.NewGuid().ToString());
        _vanity = new VanityUrlService(_ctx, NullLogger<VanityUrlService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private async Task<(global::Guild.Domain.Aggregates.Guild Guild, GuildInvite Invite)> SeedAsync(
        Action<GuildInvite>? customise = null, string? vanity = null)
    {
        var guild = global::Guild.Domain.Aggregates.Guild.Create(new global::Guild.Domain.Aggregates.CreateGuildParams
        {
            Name = "Sunday Raid Group",
            Description = "Casual mythic+",
            OwnerId = "user_1",
            OwnerSearchValue = "owner",
        });

        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = guild.Id,
            Type = InviteType.Permanent,
            MaxUses = 25,
        });

        customise?.Invoke(invite);

        if (vanity is not null)
        {
            guild.VanityUrl = vanity;
            guild.VanityInviteId = invite.Id;
        }

        _ctx.Guilds.Add(guild);
        _ctx.GuildInvites.Add(invite);
        await _ctx.SaveChangesAsync();
        _ctx.ChangeTracker.Clear();

        return (guild, invite);
    }

    private Task<Contracts.Bus.Response.ResolveInviteCardResponse> ResolveAsync(string code) =>
        ResolveInviteCardHandler.Handle(new ResolveInviteCardRequest { Code = code }, _ctx, _vanity);

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task AGeneratedCode_ResolvesTheGuildsPublicFacts()
    {
        var (guild, invite) = await SeedAsync();

        var card = (await ResolveAsync(invite.Code)).Invite;

        Assert.Multiple(() =>
        {
            Assert.That(card, Is.Not.Null);
            Assert.That(card!.GuildId, Is.EqualTo(guild.Id));
            Assert.That(card.GuildName, Is.EqualTo("Sunday Raid Group"));
            Assert.That(card.GuildDescription, Is.EqualTo("Casual mythic+"));
            Assert.That(card.MaxUses, Is.EqualTo(25));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task AVanitySlug_ResolvesToTheSameCardAsItsCode()
    {
        var (_, invite) = await SeedAsync(vanity: "sunday-raids");

        var card = (await ResolveAsync("sunday-raids")).Invite;

        Assert.Multiple(() =>
        {
            Assert.That(card, Is.Not.Null);
            Assert.That(card!.Code, Is.EqualTo(invite.Code),
                "the canonical code is emitted, not the slug, so a client's refresh survives a rename");
            Assert.That(card.GuildName, Is.EqualTo("Sunday Raid Group"));
        });
    }

    [Test]
    public async Task TheRunningUseCountAndStateAreNeverCarried()
    {
        // Everything on the card is fixed at creation.
        var (_, invite) = await SeedAsync(i => i.UseCount = 7);

        var card = (await ResolveAsync(invite.Code)).Invite!;

        Assert.That(card.GetType().GetProperties().Select(p => p.Name),
            Is.EquivalentTo(new[]
            {
                nameof(card.Code), nameof(card.GuildId), nameof(card.GuildName),
                nameof(card.GuildDescription), nameof(card.ChannelId),
                nameof(card.ExpiresAt), nameof(card.MaxUses),
            }),
            "adding a volatile or permission-sensitive field here needs the reasoning in the response's docs revisited");
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    [Test]
    public async Task ACodeNobodyMinted_ResolvesToNothing()
    {
        await SeedAsync();

        Assert.That((await ResolveAsync("NOTACODE")).Invite, Is.Null);
    }

    [Test]
    public async Task ARevokedInvite_ResolvesToNothing()
    {
        // Matching the 404 the public preview answers with.
        var (_, invite) = await SeedAsync(i => i.State = InviteState.Revoked);

        Assert.That((await ResolveAsync(invite.Code)).Invite, Is.Null);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task AnEmptyCode_ResolvesToNothingWithoutQuerying(string code)
    {
        await SeedAsync();

        Assert.That((await ResolveAsync(code)).Invite, Is.Null);
    }
}
