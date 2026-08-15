using AppEnvironment;
using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Handler.Messages;
using Messaging.Application.Previews;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Previews;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Unfurl.Contracts.Bus;

namespace Messaging.Tests.Previews;

/// <summary>
/// Server-generated cards for links that point back at this instance
/// (docs/specs/message-previews.md, "Internal links").
/// </summary>
[TestFixture]
public class InternalLinkPreviewTests
{
    private const string AppHost = "https://app.venta.gg";

    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private string _instanceUrl = "";
    private string? _appDomain;
    private string? _extraHosts;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);

        _instanceUrl = Env.GeneralConfiguration.InstanceUrl;
        _appDomain = Environment.GetEnvironmentVariable(WebClientHost.EnvironmentVariable);
        _extraHosts = Environment.GetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable);

        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable, null);
    }

    [TearDown]
    public async Task TearDown()
    {
        Env.GeneralConfiguration.InstanceUrl = _instanceUrl;
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, _appDomain);
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable, _extraHosts);
        await _context.DisposeAsync();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private async Task<Message> SeedMessage(string content)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = System.Text.Encoding.UTF8.GetBytes(content),
            ConversationId = "conv-1",
            AuthorId = "author-1",
        });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return message;
    }

    private static InviteCardInfo AnInvite(string code = "ABC23456") => new()
    {
        Code = code,
        GuildId = "gild_1",
        GuildName = "The Guild",
        GuildDescription = "Where we talk",
        ChannelId = "chan_1",
        MaxUses = 10,
    };

    /// <summary>
    /// A bus that answers the invite lookup and the final write, and records everything.
    /// </summary>
    private static FakeMessageBus Bus(InviteCardInfo? invite) => new(request => request switch
    {
        ResolveInviteCardRequest r when invite is not null && r.Code == invite.Code
            => new ResolveInviteCardResponse { Invite = invite },
        ResolveInviteCardRequest => new ResolveInviteCardResponse(),
        UnfurlUrlsRequest u => new UnfurlUrlsResponse
        {
            Results = u.Urls.Select(url => new UnfurlResult
            {
                Url = url,
                Embed = new EmbedPayload { Type = EmbedTypes.Link, Url = url, Title = "External page" },
            }).ToList(),
        },
        UpdateMessageCommand => new UpdateMessageResponse { Success = true },
        _ => throw new InvalidOperationException($"unexpected request {request.GetType().Name}"),
    });

    private async Task<(FakeMessageBus Bus, List<EmbedPayload> Embeds)> RunAsync(
        string content, InviteCardInfo? invite = null)
    {
        var message = await SeedMessage(content);
        var bus = Bus(invite);

        await UnfurlLinksHandler.Handle(
            new UnfurlMessageLinks { MessageId = message.Id },
            _repo, bus, new RecordingLogger<UnfurlLinksHandler>());

        var write = bus.Invoked.OfType<UpdateMessageCommand>().SingleOrDefault();

        return (bus, write is null ? [] : GeneratedEmbeds.Parse(write.GeneratedEmbedsJson));
    }

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task AnInviteLink_BecomesAnInviteCardWithoutTouchingTheFetcher()
    {
        var (bus, embeds) = await RunAsync($"join us {AppHost}/invite/ABC23456", AnInvite());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>(), Is.Empty,
                "an instance link must never become an outbound request");
            Assert.That(embeds, Has.Count.EqualTo(1));
            Assert.That(embeds[0].Type, Is.EqualTo(EmbedTypes.VentaInvite));
            Assert.That(embeds[0].Title, Is.EqualTo("The Guild"));
            Assert.That(embeds[0].Venta!.InviteCode, Is.EqualTo("ABC23456"));
            Assert.That(embeds[0].Venta.GuildId, Is.EqualTo("gild_1"));
            Assert.That(embeds[0].Venta.MaxUses, Is.EqualTo(10));
            Assert.That(embeds[0].Venta.Resolved, Is.True);
        });
    }

    [Test]
    public async Task AWikiLink_BecomesAStubCarryingOnlyIds()
    {
        var (bus, embeds) = await RunAsync($"see {AppHost}/wiki/gild_1/wkpg_7");

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>(), Is.Empty);
            Assert.That(embeds, Has.Count.EqualTo(1));
            Assert.That(embeds[0].Type, Is.EqualTo(EmbedTypes.VentaWikiPage));
            Assert.That(embeds[0].Venta!.GuildId, Is.EqualTo("gild_1"));
            Assert.That(embeds[0].Venta.PageId, Is.EqualTo("wkpg_7"));
        });
    }

    [Test]
    public async Task AWikiStub_CarriesNoTitleOrDescription()
    {
        // THE security assertion in this file.
        var (_, embeds) = await RunAsync($"see {AppHost}/wiki/gild_1/wkpg_7");

        Assert.Multiple(() =>
        {
            Assert.That(embeds[0].Title, Is.Null);
            Assert.That(embeds[0].Description, Is.Null);
            Assert.That(embeds[0].Venta!.Resolved, Is.False,
                "false is the client's signal to fetch the name per viewer instead");
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task AMixedMessage_SendsOnlyTheExternalLinkToTheFetcher()
    {
        var (bus, embeds) = await RunAsync(
            $"read https://example.com/article then join {AppHost}/invite/ABC23456", AnInvite());

        var fetched = bus.Invoked.OfType<UnfurlUrlsRequest>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(fetched.Urls, Is.EqualTo(new[] { "https://example.com/article" }));
            Assert.That(embeds, Has.Count.EqualTo(2));
            Assert.That(embeds[0].Type, Is.EqualTo(EmbedTypes.Link),
                "cards keep the order the links appear in the message, not the order they resolved");
            Assert.That(embeds[1].Type, Is.EqualTo(EmbedTypes.VentaInvite));
        });
    }

    [Test]
    public async Task AnAngleBracketedInstanceLink_GetsNoCard()
    {
        // The sender-side opt-out has to keep working for our own links too, or the only way to
        // mention an invite without dragging a card into the channel would be to break the URL.
        var (bus, embeds) = await RunAsync($"quietly <{AppHost}/invite/ABC23456>", AnInvite());

        Assert.Multiple(() =>
        {
            Assert.That(embeds, Is.Empty);
            Assert.That(bus.Invoked, Is.Empty, "nothing was resolved, so nothing was written either");
        });
    }

    [Test]
    public async Task AnInstanceUrlWithNoKnownShape_IsNeitherCardedNorFetched()
    {
        var (bus, embeds) = await RunAsync($"look at {AppHost}/settings/appearance");

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>(), Is.Empty,
                "still our own host, so still not something to fetch");
            Assert.That(embeds, Is.Empty);
        });
    }

    [Test]
    public async Task AThirdPartyInviteUrl_StillGoesToTheFetcher()
    {
        // Discord's invites have the same path shape. External links must behave exactly as before.
        var (bus, embeds) = await RunAsync("https://discord.com/invite/ABC23456");

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>().Single().Urls,
                Is.EqualTo(new[] { "https://discord.com/invite/ABC23456" }));
            Assert.That(embeds[0].Type, Is.EqualTo(EmbedTypes.Link));
        });
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    [Test]
    public async Task AnInviteCodeThatDoesNotExist_GetsNoCardAndNoWrite()
    {
        // No card at all rather than an "invalid invite" one: an instance should not confirm which
        // codes exist to somebody guessing, and a typo'd code reads as the plain text it is.
        var (bus, embeds) = await RunAsync($"join {AppHost}/invite/NOTACODE", AnInvite());

        Assert.Multiple(() =>
        {
            Assert.That(embeds, Is.Empty);
            Assert.That(bus.Invoked.OfType<UpdateMessageCommand>(), Is.Empty,
                "an empty result must not rewrite the row or wake every viewer's client");
        });
    }

    [Test]
    public async Task AMalformedInstanceLink_IsNeitherCardedNorFetched()
    {
        var (bus, embeds) = await RunAsync($"broken {AppHost}/invite/%2Fetc%2Fpasswd");

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>(), Is.Empty);
            Assert.That(embeds, Is.Empty);
        });
    }

    [Test]
    public async Task ASelfHostedInstance_ResolvesItsOwnLinksAndFetchesOurs()
    {
        // The self-hosting case, and the reason the host set is configuration.
        Env.GeneralConfiguration.InstanceUrl = "https://api.chat.example.org";

        var (bus, embeds) = await RunAsync(
            $"a {AppHost}/invite/ABC23456 and b https://app.chat.example.org/invite/ABC23456",
            AnInvite());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>().Single().Urls,
                Is.EqualTo(new[] { $"{AppHost}/invite/ABC23456" }),
                "venta.gg is a third party to this instance");
            Assert.That(embeds.Count(e => e.Type == EmbedTypes.VentaInvite), Is.EqualTo(1));
        });
    }

    // ── The apex, which is nobody's sibling ──────────────────────────────────

    [Test]
    public async Task AnInviteOnTheApexDomain_GoesToTheFetcher_UntilItIsConfigured()
    {
        // The bug this pins.
        var (bus, embeds) = await RunAsync("join us https://venta.gg/invite/ABC23456", AnInvite());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>().Single().Urls,
                Is.EqualTo(new[] { "https://venta.gg/invite/ABC23456" }));
            Assert.That(embeds[0].Type, Is.EqualTo(EmbedTypes.Link),
                "the scraped marketing card, which is what shipped");
        });
    }

    [Test]
    public async Task AnInviteOnTheApexDomain_BecomesAnInviteCard_WhenItIsConfigured()
    {
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable, "venta.gg");

        var (bus, embeds) = await RunAsync("join us https://venta.gg/invite/ABC23456", AnInvite());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>(), Is.Empty,
                "and the fetcher stops being pointed at our own apex on every paste");
            Assert.That(embeds, Has.Count.EqualTo(1));
            Assert.That(embeds[0].Type, Is.EqualTo(EmbedTypes.VentaInvite));
            Assert.That(embeds[0].Venta!.InviteCode, Is.EqualTo("ABC23456"));
        });
    }

    [Test]
    public async Task ConfiguringTheApex_DoesNotAffectItsSiblings()
    {
        // Additive: the two derived entries are still there, so the deployment that adds an apex
        // does not lose the host its own API is on.
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable, "venta.gg");

        var (bus, embeds) = await RunAsync($"still ours {AppHost}/invite/ABC23456", AnInvite());

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>(), Is.Empty);
            Assert.That(embeds[0].Type, Is.EqualTo(EmbedTypes.VentaInvite));
        });
    }

    [Test]
    public async Task AConfiguredApex_GetsNoCardForAPathWeDoNotKnow()
    {
        // The trade-off, stated.
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable, "venta.gg");

        var (bus, embeds) = await RunAsync("read https://venta.gg/blog/whats-new");

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<UnfurlUrlsRequest>(), Is.Empty);
            Assert.That(embeds, Is.Empty);
        });
    }

    // ── The builder in isolation ─────────────────────────────────────────────

    [Test]
    public async Task AGuildNameLongerThanTheEmbedLimit_IsClamped()
    {
        // A guild's name is typed by its owner, so it is third-party text on the same footing as a
        // scraped og:title, and it shares the 6000-character budget with every other embed.
        var invite = AnInvite();
        invite.GuildName = new string('g', EmbedLimits.Title + 500);

        var (_, embeds) = await RunAsync($"{AppHost}/invite/ABC23456", invite);

        Assert.That(embeds[0].Title, Has.Length.EqualTo(EmbedLimits.Title));
    }

    [Test]
    public async Task TheResolver_ReturnsNullForAnInviteNobodyMinted()
    {
        var link = new InternalLink
        {
            Kind = InternalLinkKind.Invite,
            Url = $"{AppHost}/invite/NOTACODE",
            Values = new Dictionary<string, string> { ["code"] = "NOTACODE" },
        };

        var embed = await InternalLinkEmbeds.ResolveAsync(link, Bus(AnInvite()));

        Assert.That(embed, Is.Null);
    }
}
