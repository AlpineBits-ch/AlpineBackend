using Alba;
using Domain;
using Identity.Application.Consumers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Consumers;

/// <summary>
/// The batched linked-account lookup behind the <c>connections</c> profile field (privacy spec
/// T2-17). Real Postgres, for the same reason as <see cref="GetUserPrivacySettingsHandlerTests"/>.
///
/// <para>Steam is the only link type this codebase has. The negative cases are the point: a raw
/// SteamID64 is a stable cross-platform correlation handle, so what has to hold is that it does not
/// leave the service for an account that has switched connections off, and that the response shape
/// admits a second provider without a break.</para>
/// </summary>
[TestFixture]
public class GetUserConnectionsHandlerTests
{
    private static IAlbaHost Host => AppFixture.Host;

    private IServiceScope _scope = null!;
    private MicroserviceContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = Host.Services.CreateScope();
        _ctx = _scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (await _ctx.Users.AnyAsync())
        {
            _ctx.Users.RemoveRange(_ctx.Users);
            await _ctx.SaveChangesAsync();
        }
        _scope.Dispose();
    }

    private async Task<string> SeedUserAsync(string? steamId, Visibility visibility = Visibility.Friends)
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = $"cx-{Guid.NewGuid():N}@example.com",
            PhoneNumber = $"+4179{Random.Shared.Next(1000000, 9999999)}",
            Username = $"cx{Guid.NewGuid():N}"[..15],
            BirthDate = new DateOnly(1990, 1, 1),
        });

        user.SteamId = steamId;
        user.UserPrivacySettings!.ConnectionsVisibility = visibility;

        _ctx.Users.Add(user);
        await _ctx.SaveChangesAsync();
        return user.Id;
    }

    private Task<GetUserConnectionsResponse> Get(params string[] userIds) =>
        GetUserConnectionsHandler.Handle(new GetUserConnectionsRequest { UserIds = userIds.ToList() }, _ctx);

    private static ICollection<ExternalConnectionSummary> For(GetUserConnectionsResponse response, string userId) =>
        response.Users.Single(u => u.UserId == userId).Connections;

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ReportsALinkedSteamAccount()
    {
        var userId = await SeedUserAsync("76561198000000000");

        var connections = For(await Get(userId), userId);

        Assert.That(connections, Has.Count.EqualTo(1));
        var steam = connections.Single();
        Assert.Multiple(() =>
        {
            Assert.That(steam.Type, Is.EqualTo(ExternalConnectionTypes.Steam));
            Assert.That(steam.ExternalId, Is.EqualTo("76561198000000000"));
            Assert.That(steam.DisplayName, Is.Null, "nothing here fetches a Steam persona name");
        });
    }

    [Test]
    public async Task Handle_AnswersEveryIdInOneRoundTrip()
    {
        var linked = await SeedUserAsync("76561198000000001");
        var unlinked = await SeedUserAsync(null);

        var response = await Get(linked, unlinked);

        Assert.That(response.Users.Select(u => u.UserId), Is.EquivalentTo(new[] { linked, unlinked }));
        Assert.Multiple(() =>
        {
            Assert.That(For(response, linked), Has.Count.EqualTo(1));
            Assert.That(For(response, unlinked), Is.Empty);
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_EmptyRequest_ReturnsNothingRatherThanEveryone()
    {
        await SeedUserAsync("76561198000000000");

        Assert.That((await Get()).Users, Is.Empty);
    }

    [Test]
    public async Task Handle_DuplicateAndBlankIds_AreNormalised()
    {
        var userId = await SeedUserAsync("76561198000000000");

        var response = await Get(userId, userId, "", "   ");

        Assert.That(response.Users, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Handle_DoesNotTrackTheRowsItReads()
    {
        var userId = await SeedUserAsync("76561198000000000");
        _ctx.ChangeTracker.Clear();

        await Get(userId);

        Assert.That(_ctx.ChangeTracker.Entries<ApplicationUser>(), Is.Empty);
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_VisibilityNobody_WithholdsTheSteamId()
    {
        // No viewer could ever be admitted, so the correlation handle never leaves the service.
        var userId = await SeedUserAsync("76561198000000000", Visibility.Nobody);

        Assert.That(For(await Get(userId), userId), Is.Empty);
    }

    [Test]
    public async Task Handle_NothingLinked_IsAnEmptyListNotAMissingEntry()
    {
        var userId = await SeedUserAsync(null, Visibility.Everyone);

        var response = await Get(userId);

        Assert.That(response.Users, Has.Count.EqualTo(1));
        Assert.That(For(response, userId), Is.Empty);
    }

    [Test]
    public async Task Handle_UnknownUser_IsAnsweredWithAnEmptyList()
    {
        var response = await Get("user_doesnotexist");

        Assert.That(response.Users, Has.Count.EqualTo(1));
        Assert.That(For(response, "user_doesnotexist"), Is.Empty);
    }

    [Test]
    public async Task Handle_PurgedAccount_ReportsNoConnections()
    {
        // Tombstone() clears SteamId (it always did) - this pins that the new read path honours it.
        var userId = await SeedUserAsync("76561198000000000", Visibility.Everyone);

        var user = await _ctx.Users.FirstAsync(u => u.Id == userId);
        user.Tombstone();
        await _ctx.SaveChangesAsync();

        Assert.That(For(await Get(userId), userId), Is.Empty);
    }

    [Test]
    public async Task Handle_AccountWithNoSettingsRow_WithholdsTheSteamId()
    {
        // Stricter than the shipped Friends default on purpose: a missing row means something
        // unexpected happened, and the safe reading is "do not hand out the correlation handle".
        var userId = await SeedUserAsync("76561198000000000");

        var settings = await _ctx.UserPrivacySettings.FirstAsync(p => p.UserId == userId);
        _ctx.UserPrivacySettings.Remove(settings);
        await _ctx.SaveChangesAsync();
        _ctx.ChangeTracker.Clear();

        Assert.That(For(await Get(userId), userId), Is.Empty);
    }
}
