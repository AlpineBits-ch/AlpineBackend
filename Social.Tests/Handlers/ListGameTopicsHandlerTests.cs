using Social.Api.Integration.GameCatalog;
using Social.Contracts.Bus.Integration.Request;
using Social.Domain.Aggregate;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

/// <summary>The bus-side page reader Discovery's catalog mirror pages through.</summary>
[TestFixture]
public class ListGameTopicsHandlerTests
{
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestSocialContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private GameApplication AddApp(string id, string name, bool isEnabled = true)
    {
        var app = new GameApplication { Id = id, Name = name, IsEnabled = isEnabled };
        _context.GameApplications.Add(app);
        return app;
    }

    [Test]
    public async Task Handle_MoreRowsThanTheLimit_ReturnsOnlyThePageAndACursor()
    {
        AddApp("gapp_1", "A");
        AddApp("gapp_2", "B");
        AddApp("gapp_3", "C");
        await _context.SaveChangesAsync();

        var response = await ListGameTopicsHandler.Handle(
            new ListGameTopicsRequest { Limit = 2 }, _context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Topics.Select(t => t.Id), Is.EqualTo(new[] { "gapp_1", "gapp_2" }),
                "ordering must be ordinal on the id, matching GameCatalogEndpoint");
            Assert.That(response.NextCursor, Is.EqualTo("gapp_2"));
        });
    }

    [Test]
    public async Task Handle_RowsFitInTheLimit_ReturnsNoCursor()
    {
        AddApp("gapp_1", "A");
        AddApp("gapp_2", "B");
        await _context.SaveChangesAsync();

        var response = await ListGameTopicsHandler.Handle(
            new ListGameTopicsRequest { Limit = 10 }, _context, CancellationToken.None);

        Assert.That(response.NextCursor, Is.Null, "a cursor here would send the caller into an infinite page loop");
    }

    [Test]
    public async Task Handle_After_ContinuesStrictlyPastTheCursor()
    {
        AddApp("gapp_1", "A");
        AddApp("gapp_2", "B");
        AddApp("gapp_3", "C");
        await _context.SaveChangesAsync();

        var response = await ListGameTopicsHandler.Handle(
            new ListGameTopicsRequest { Limit = 10, After = "gapp_1" }, _context, CancellationToken.None);

        Assert.That(response.Topics.Select(t => t.Id), Is.EqualTo(new[] { "gapp_2", "gapp_3" }));
    }

    [Test]
    public async Task Handle_DisabledApplication_IsStillReturned()
    {
        // Discovery's sync disables its mirror row from this flag; if the handler dropped disabled
        // rows here, a game switched off upstream would linger enabled on Discovery's side forever.
        AddApp("gapp_1", "Off", isEnabled: false);
        await _context.SaveChangesAsync();

        var response = await ListGameTopicsHandler.Handle(
            new ListGameTopicsRequest(), _context, CancellationToken.None);

        Assert.That(response.Topics.Single().IsEnabled, Is.False);
    }
}
