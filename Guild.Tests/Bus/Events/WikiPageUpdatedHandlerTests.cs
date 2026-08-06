using Guild.Application.Bus.Events.Wiki;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Events.Wiki;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Events;

/// <summary>Covers the watcher half of <see cref="WikiPageUpdatedHandler"/>.</summary>
[TestFixture]
public class WikiPageUpdatedHandlerTests
{
    private const string GuildId = "gild-1";
    private const string EditorId = "user-editor";
    private const string WatcherId = "user-watcher";

    private TestGuildContext _context = null!;
    private FakeHubContext _hub = null!;
    private WikiPageUpdatedHandler _handler = null!;
    private WikiPage _page = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _hub = new FakeHubContext();
        _handler = new WikiPageUpdatedHandler();

        _page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = GuildId, Title = "Runbook", Content = "v1", AuthorId = EditorId,
        });
        _context.WikiPages.Add(_page);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private GuildHydrateService Hydrate() =>
        new(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);

    private Task RunAsync() => _handler.Handle(
        new WikiPageUpdated { PageId = _page.Id, GuildId = GuildId, EditorId = EditorId },
        _hub, Hydrate(), _context);

    private async Task WatchAsync(string userId)
    {
        _context.WikiPageWatchers.Add(WikiPageWatcher.Create(new CreateWikiPageWatcherParams
        {
            PageId = _page.Id, GuildId = GuildId, UserId = userId,
        }));
        await _context.SaveChangesAsync();
    }

    private List<string> WatchRecipients() =>
        ((FakeHubClients)_hub.Clients).RecipientsOf("guild.WikiPageWatchedUpdate");

    [Test]
    public async Task Update_NotifiesWatchers()
    {
        await WatchAsync(WatcherId);

        await RunAsync();

        Assert.That(WatchRecipients(), Is.EqualTo(new[] { WatcherId }));
    }

    // Nobody wants a notification about the edit they just made.
    [Test]
    public async Task Update_DoesNotNotifyTheEditorEvenWhenTheyWatch()
    {
        await WatchAsync(EditorId);
        await WatchAsync(WatcherId);

        await RunAsync();

        Assert.That(WatchRecipients(), Does.Not.Contain(EditorId));
        Assert.That(WatchRecipients(), Does.Contain(WatcherId));
    }

    // No watchers means no send at all, not a send addressed to nobody - the guild-wide broadcast
    // above it still has to happen either way.
    [Test]
    public async Task Update_WithNoWatchers_SendsOnlyTheGuildBroadcast()
    {
        await RunAsync();

        var sent = ((FakeHubClients)_hub.Clients).SentMessages.Select(m => m.Method).ToList();
        Assert.That(sent, Is.EqualTo(new[] { "guild.WikiPageUpdated" }));
    }

    // A watcher on some other page must not hear about this one.
    [Test]
    public async Task Update_DoesNotNotifyWatchersOfOtherPages()
    {
        var other = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = GuildId, Title = "Other", Content = "x", AuthorId = EditorId,
        });
        _context.WikiPages.Add(other);
        _context.WikiPageWatchers.Add(WikiPageWatcher.Create(new CreateWikiPageWatcherParams
        {
            PageId = other.Id, GuildId = GuildId, UserId = "user-elsewhere",
        }));
        await _context.SaveChangesAsync();

        await RunAsync();

        Assert.That(WatchRecipients(), Is.Empty);
    }
}
