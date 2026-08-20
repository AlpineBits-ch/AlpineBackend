using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Guild.Tests.Migrations;

/// <summary>
/// The partial unique index on (scene_channel_id, persona_id) WHERE status = 'pending'. EF InMemory
/// cannot enforce it, so the rule that a character queues once but may ask again after a refusal is
/// only ever proved here.
/// </summary>
[TestFixture]
public class SceneJoinRequestIndexTests
{
    private const string SceneChannelId = "scene-migration";
    private const string PersonaId = "pers_mayor";

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        await MigrationSqlHarness.SeedGuildAsync();

        await using var context = new PostgresGuildContext();

        context.Channels.Add(new Guild.Domain.Aggregates.Channel
        {
            Id = SceneChannelId,
            GuildId = MigrationSqlHarness.GuildId,
            Name = "The Siege of Blackwater",
            Type = ChannelType.Scene,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();
    }

    [Test]
    public async Task ASecondPendingRow_ForTheSameCharacter_IsRefused()
    {
        await AddAsync(SceneJoinRequestStatus.Pending);

        Assert.That(
            async () => await AddAsync(SceneJoinRequestStatus.Pending),
            Throws.InstanceOf<DbUpdateException>()
                .With.InnerException.InstanceOf<PostgresException>());
    }

    [Test]
    public async Task AskingAgainAfterARefusal_IsAllowed()
    {
        await AddAsync(SceneJoinRequestStatus.Denied);
        await AddAsync(SceneJoinRequestStatus.Pending);

        await using var context = new PostgresGuildContext();

        Assert.That(
            await context.SceneJoinRequests.CountAsync(r => r.SceneChannelId == SceneChannelId),
            Is.EqualTo(2));
    }

    [Test]
    public async Task TwoDecidedRows_ForTheSameCharacter_Coexist()
    {
        await AddAsync(SceneJoinRequestStatus.Denied);
        await AddAsync(SceneJoinRequestStatus.Denied);

        await using var context = new PostgresGuildContext();

        Assert.That(
            await context.SceneJoinRequests.CountAsync(r => r.SceneChannelId == SceneChannelId),
            Is.EqualTo(2));
    }

    private static async Task AddAsync(SceneJoinRequestStatus status)
    {
        await using var context = new PostgresGuildContext();

        var request = SceneJoinRequest.Create(new CreateSceneJoinRequestParams
        {
            SceneChannelId = SceneChannelId,
            GuildId = MigrationSqlHarness.GuildId,
            PersonaId = PersonaId,
            RequestedByUserId = "user-player",
        });

        if (status != SceneJoinRequestStatus.Pending)
            request.Decide(status, "user-gm", "Not this arc.", DateTimeOffset.UtcNow);

        context.SceneJoinRequests.Add(request);
        await context.SaveChangesAsync();
    }
}
