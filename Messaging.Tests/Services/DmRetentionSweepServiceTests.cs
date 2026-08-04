using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Messaging.Tests.Services;

/// <summary>
/// T2-22. The sweep deletes a user's <b>own</b> messages once they pass that user's retention
/// window - and, above all, nobody else's.
/// </summary>
[TestFixture]
public class DmRetentionSweepServiceTests
{
    private TestMessagingContext _context = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _bus = new FakeMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ConversationMember MakeMember(string id, string userId, string conversationId) => new()
    {
        Id = id,
        UserId = userId,
        ConversationId = conversationId,
        PublicKey = [],
        CachedUserName = "test-user",
        CachedUserHash = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<Message> SeedMessage(string conversationId, string authorId, int daysOld)
    {
        var message = Message.Create(new CreateMessageParams
        {
            Content = "hello"u8.ToArray(),
            ConversationId = conversationId,
            AuthorId = authorId,
        });

        var at = DateTimeOffset.UtcNow.AddDays(-daysOld);
        message.CreatedAt = at;
        message.UpdatedAt = at;

        _context.Messages.Add(message);
        await _context.SaveChangesAsync();

        // The sweep reads AsNoTracking and then removes what it read, which is only legal against a
        // context that is not already tracking those rows.
        _context.ChangeTracker.Clear();
        return message;
    }

    /// <summary>
    /// The sweep resolves everything through a scope, exactly as the account-deletion sweep it is
    /// modelled on does.
    /// </summary>
    private DmRetentionSweepService Sweep(
        int? retentionDays,
        DmRetentionOptions? options = null,
        IReadOnlyCollection<string>? users = null,
        IMessageRepository? repo = null,
        ILogger<DmRetentionSweepService>? logger = null)
    {
        var ids = users ?? ["user-1"];

        var privacy = TestPrivacyServices.Build(
            _bus,
            retentionDays is null
                ? []
                : ids.Select(id => TestPrivacyServices.With(id, s => s.DmRetentionDays = retentionDays)).ToList());

        var services = new ServiceCollection();
        services.AddSingleton<MicroserviceContext>(_context);
        services.AddSingleton(repo ?? new EfCoreMessageRepository(_context));
        services.AddSingleton(privacy.Privacy);
        services.AddSingleton<IMessageBus>(_bus);

        return new DmRetentionSweepService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            // The Scylla kill switch defaults to whatever the environment says; every test in this
            // fixture runs the EF repository, which the gate does not apply to, but a test that
            // deliberately runs the Scylla one passes its own options.
            options ?? new DmRetentionOptions(),
            logger ?? NullLogger<DmRetentionSweepService>.Instance);
    }

    private async Task<DmRetentionCursor?> ReadCursor() =>
        await _context.DmRetentionCursors
            .FirstOrDefaultAsync(c => c.Id == DmRetentionCursor.SingletonId);

    [Test]
    public async Task DeletesTheUsersOwnMessagesPastTheWindow()
    {
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var old = await SeedMessage("conv-1", "user-1", daysOld: 40);
        var recent = await SeedMessage("conv-1", "user-1", daysOld: 5);

        await Sweep(retentionDays: 30).SweepAsync(CancellationToken.None);

        var remaining = await _context.Messages.Select(m => m.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain(old.Id));
            Assert.That(remaining, Does.Contain(recent.Id));
        });
    }

    [Test]
    public async Task NeverDeletesTheOtherSidesMessages()
    {
        // The line between a retention control and a history-rewriting one.
        _context.Members.AddRange(
            MakeMember("m-1", "user-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        var mine = await SeedMessage("conv-1", "user-1", daysOld: 40);
        var theirs = await SeedMessage("conv-1", "user-2", daysOld: 40);

        await Sweep(retentionDays: 30).SweepAsync(CancellationToken.None);

        var remaining = await _context.Messages.Select(m => m.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain(mine.Id));
            Assert.That(remaining, Does.Contain(theirs.Id),
                "user-2 set no retention window, and it would not be user-1's to apply if they had");
        });
    }

    [Test]
    public async Task WithNoRetentionSet_DeletesNothing()
    {
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        await SeedMessage("conv-1", "user-1", daysOld: 4000);

        await Sweep(retentionDays: null).SweepAsync(CancellationToken.None);

        Assert.That(await _context.Messages.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task SweepsEveryConversationTheUserIsIn()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-1", "conv-1"),
            MakeMember("m-2", "user-1", "conv-2"));
        await _context.SaveChangesAsync();

        await SeedMessage("conv-1", "user-1", daysOld: 40);
        await SeedMessage("conv-2", "user-1", daysOld: 40);

        await Sweep(retentionDays: 30).SweepAsync(CancellationToken.None);

        Assert.That(await _context.Messages.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task PublishesMessageDeletedSoTheOrdinaryCleanupRuns()
    {
        // Search-index removal, the realtime "this is gone" and the bot dispatch all hang off this
        // event; a retention-shaped variant of each would be a second implementation to drift.
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var old = await SeedMessage("conv-1", "user-1", daysOld: 40);

        await Sweep(retentionDays: 30).SweepAsync(CancellationToken.None);

        Assert.That(_bus.Published.OfType<MessageDeleted>().Select(e => e.MessageId),
            Is.EquivalentTo(new[] { old.Id }));
    }

    [Test]
    public async Task DoesNotStallBehindAPageOfSomebodyElsesMessages()
    {
        // The failure a naive "oldest N, then filter by author" sweep has: if the oldest page is
        // entirely the other party's, the user's own older messages are never reached and the sweep
        // makes no progress on any tick, forever.
        _context.Members.AddRange(
            MakeMember("m-1", "user-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-1"));
        await _context.SaveChangesAsync();

        for (var i = 0; i < 5; i++) await SeedMessage("conv-1", "user-2", daysOld: 100 - i);
        var mine = await SeedMessage("conv-1", "user-1", daysOld: 40);

        var options = new DmRetentionOptions { PageSize = 2 };

        await Sweep(retentionDays: 30, options).SweepAsync(CancellationToken.None);

        var remaining = await _context.Messages.Select(m => m.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain(mine.Id));
            Assert.That(remaining, Has.Count.EqualTo(5), "every one of the other party's messages survives");
        });
    }

    [Test]
    public async Task RespectsThePerUserDeleteBudget()
    {
        // The first sweep after somebody enables a window on years of history must not be one
        // enormous burst of deletes and MessageDeleted fan-out. Successive ticks finish the job.
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        for (var i = 0; i < 6; i++) await SeedMessage("conv-1", "user-1", daysOld: 100 - i);

        var options = new DmRetentionOptions { MaxDeletesPerUserPerTick = 2 };

        await Sweep(retentionDays: 30, options).SweepAsync(CancellationToken.None);

        Assert.That(await _context.Messages.CountAsync(), Is.EqualTo(4));
    }

    [Test]
    public void OptionsBindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:DmRetention:SweepIntervalSeconds"] = "90",
                ["Messaging:DmRetention:MaxDeletesPerUserPerTick"] = "7",
                ["Messaging:DmRetention:RotationLagWarningMultiple"] = "9",
                ["Messaging:DmRetention:ScyllaDeleteEnabled"] = "true",
            })
            .Build();

        var options = DmRetentionOptions.FromConfiguration(configuration);

        Assert.Multiple(() =>
        {
            Assert.That(options.SweepInterval, Is.EqualTo(TimeSpan.FromSeconds(90)));
            Assert.That(options.MaxDeletesPerUserPerTick, Is.EqualTo(7));
            Assert.That(options.RotationLagWarningMultiple, Is.EqualTo(9));
            Assert.That(options.ScyllaDeleteEnabled, Is.True);
            Assert.That(options.PageSize, Is.EqualTo(new DmRetentionOptions().PageSize), "untouched keys keep their default");
        });
    }

    [Test]
    public void TheScyllaDeletePathIsOffUnlessSomebodyTurnsItOn()
    {
        // The defaults a deployment gets if it configures nothing.
        Assert.That(new DmRetentionOptions().ScyllaDeleteEnabled, Is.False);
    }

    [Test]
    public void AnUnparseableScyllaFlagDoesNotSilentlyDisableThePath()
    {
        // "yes" is not a bool.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Messaging:DmRetention:ScyllaDeleteEnabled"] = "yes",
            })
            .Build();

        Assert.That(DmRetentionOptions.FromConfiguration(configuration).ScyllaDeleteEnabled,
            Is.EqualTo(new DmRetentionOptions().ScyllaDeleteEnabled));
    }

    // ══════════════════════════════════════════════════════════════════════════ Defect 1: the
    // rotation.

    /// <summary>Three accounts, each with one message past their window, each in their own
    /// conversation.</summary>
    private async Task<Dictionary<string, Message>> SeedThreeUsers()
    {
        var seeded = new Dictionary<string, Message>();

        for (var i = 1; i <= 3; i++)
        {
            _context.Members.Add(MakeMember($"m-{i}", $"user-{i}", $"conv-{i}"));
            await _context.SaveChangesAsync();
            seeded[$"user-{i}"] = await SeedMessage($"conv-{i}", $"user-{i}", daysOld: 40);
        }

        return seeded;
    }

    [Test]
    public async Task ReachesEveryUser_NotJustTheFirstPageOfThem()
    {
        // The defect, exactly: with MaxUsersPerTick below the account count, the old sweep took the
        // first N ordered by id on every single tick, so users 2 and 3 were never swept at all and
        // their retention setting did nothing, silently, forever.
        var seeded = await SeedThreeUsers();
        var users = seeded.Keys.ToList();
        var options = new DmRetentionOptions { MaxUsersPerTick = 1 };

        var sweep = Sweep(retentionDays: 30, options, users);
        for (var tick = 0; tick < 3; tick++) await sweep.SweepAsync(CancellationToken.None);

        Assert.That(await _context.Messages.CountAsync(), Is.Zero,
            "an account past the per-tick cap was never reached");
    }

    [Test]
    public async Task OneTickOnlyExaminesOnePageOfUsers()
    {
        // The cap is still a cap - the fix is rotation, not "sweep everybody every tick".
        var seeded = await SeedThreeUsers();
        var options = new DmRetentionOptions { MaxUsersPerTick = 1 };

        var result = await Sweep(retentionDays: 30, options, seeded.Keys.ToList())
            .SweepAsync(CancellationToken.None);

        var remaining = await _context.Messages.Select(m => m.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.UsersExamined, Is.EqualTo(1));
            Assert.That(remaining, Does.Not.Contain(seeded["user-1"].Id));
            Assert.That(remaining, Does.Contain(seeded["user-2"].Id));
            Assert.That(remaining, Does.Contain(seeded["user-3"].Id));
        });
    }

    [Test]
    public async Task TheCursorSurvivesARestart()
    {
        // A cursor held in a field would restart at the top on every deploy, which on a deployment
        // that restarts more often than a rotation takes is the original bug with extra steps.
        var seeded = await SeedThreeUsers();
        var users = seeded.Keys.ToList();
        var options = new DmRetentionOptions { MaxUsersPerTick = 1 };

        await Sweep(retentionDays: 30, options, users).SweepAsync(CancellationToken.None);

        // A different service instance, a different scope factory, a different provider - the only
        // thing carried over is the row in the store.
        var afterRestart = await Sweep(retentionDays: 30, options, users).SweepAsync(CancellationToken.None);

        var remaining = await _context.Messages.Select(m => m.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(afterRestart.UsersExamined, Is.EqualTo(1));
            Assert.That(remaining, Does.Not.Contain(seeded["user-2"].Id),
                "the sweep restarted from the top instead of resuming");
            Assert.That(remaining, Does.Contain(seeded["user-3"].Id));
        });
    }

    [Test]
    public async Task AUserAddedBehindTheCursorIsReachedOnTheNextRotation()
    {
        // The set is not stable between ticks.
        _context.Members.AddRange(
            MakeMember("m-2", "user-2", "conv-2"),
            MakeMember("m-3", "user-3", "conv-3"));
        await _context.SaveChangesAsync();

        await SeedMessage("conv-2", "user-2", daysOld: 40);
        await SeedMessage("conv-3", "user-3", daysOld: 40);

        var users = new[] { "user-1", "user-2", "user-3" };
        var options = new DmRetentionOptions { MaxUsersPerTick = 1 };
        var sweep = Sweep(retentionDays: 30, options, users);

        await sweep.SweepAsync(CancellationToken.None); // user-2

        // Joins behind the cursor.
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();
        var latecomer = await SeedMessage("conv-1", "user-1", daysOld: 40);

        await sweep.SweepAsync(CancellationToken.None); // user-3
        await sweep.SweepAsync(CancellationToken.None); // runs off the end, rotation wraps
        await sweep.SweepAsync(CancellationToken.None); // user-1, from the top

        Assert.That(await _context.Messages.Select(m => m.Id).ToListAsync(),
            Does.Not.Contain(latecomer.Id),
            "an account added behind the cursor was never reached by any later rotation");
    }

    [Test]
    public async Task RunningOffTheEndCompletesTheRotationAndResetsThePosition()
    {
        var seeded = await SeedThreeUsers();
        var options = new DmRetentionOptions { MaxUsersPerTick = 2 };
        var sweep = Sweep(retentionDays: 30, options, seeded.Keys.ToList());

        var first = await sweep.SweepAsync(CancellationToken.None);   // user-1, user-2 (a full page)
        var second = await sweep.SweepAsync(CancellationToken.None);  // user-3, a short page: the tail

        var cursor = await ReadCursor();

        Assert.Multiple(() =>
        {
            Assert.That(first.RotationCompleted, Is.False);
            Assert.That(second.RotationCompleted, Is.True);
            Assert.That(cursor!.RotationsCompleted, Is.EqualTo(1));
            Assert.That(cursor.LastUserId, Is.Empty, "the next rotation must start from the top");
            Assert.That(cursor.UsersSeenThisRotation, Is.Zero);
        });
    }

    [Test]
    public async Task LogsTheCompletedRotationSoAnOperatorCanSeeItKeepingUp()
    {
        await SeedThreeUsers();
        var log = new RecordingLogger<DmRetentionSweepService>();

        await Sweep(retentionDays: 30, users: ["user-1", "user-2", "user-3"], logger: log)
            .SweepAsync(CancellationToken.None);

        Assert.That(log.Any(LogLevel.Information, "completed rotation"), Is.True);
    }

    [Test]
    public async Task WarnsWhenARotationOutlastsItsBudget()
    {
        // A rotation that needs many multiples of the tick interval is one where the per-tick cap is
        // too low for the account count, and the only symptom users see is that their retention
        // window is honoured days late. It has to be visible before that.
        await SeedThreeUsers();
        var log = new RecordingLogger<DmRetentionSweepService>();
        var options = new DmRetentionOptions
        {
            SweepInterval = TimeSpan.FromSeconds(1),
            RotationLagWarningMultiple = 2,
            MaxUsersPerTick = 1,
        };

        var sweep = Sweep(retentionDays: 30, options, ["user-1", "user-2", "user-3"], logger: log);
        await sweep.SweepAsync(CancellationToken.None);

        // Backdate the rotation's start rather than sleeping: the budget here is two seconds and a
        // test that waits it out is a test that gets deleted the first time CI is slow.
        var cursor = await ReadCursor();
        cursor!.RotationStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _context.SaveChangesAsync();

        await sweep.SweepAsync(CancellationToken.None);

        Assert.That(log.Any(LogLevel.Warning, "has been running"), Is.True);
    }

    [Test]
    public async Task TheLagWarningIsIssuedOnceARotation()
    {
        // A six-hourly job that warns on every tick is a job whose warnings get filtered out.
        await SeedThreeUsers();
        var log = new RecordingLogger<DmRetentionSweepService>();
        var options = new DmRetentionOptions
        {
            SweepInterval = TimeSpan.FromSeconds(1),
            RotationLagWarningMultiple = 2,
            MaxUsersPerTick = 1,
        };

        var sweep = Sweep(retentionDays: 30, options, ["user-1", "user-2", "user-3"], logger: log);
        await sweep.SweepAsync(CancellationToken.None);

        var cursor = await ReadCursor();
        cursor!.RotationStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _context.SaveChangesAsync();

        await sweep.SweepAsync(CancellationToken.None);
        await sweep.SweepAsync(CancellationToken.None);

        Assert.That(log.Messages(LogLevel.Warning).Count(m => m.Contains("has been running")),
            Is.EqualTo(1));
    }

    [Test]
    public void TheRotationPageTranslatesOnTheRealProvider()
    {
        // Everything else in this fixture runs on EF InMemory, which evaluates string.Compare in
        // process and so cannot fail on a predicate Npgsql has no translation for.
        var builder = new DbContextOptionsBuilder<MicroserviceContext>();
        builder.UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused", npgsql =>
        {
            npgsql.MapEnum<ChannelEncryptionState>();
            npgsql.MapEnum<MessageType>();
            npgsql.MapEnum<AttachmentState>();
            npgsql.MapEnum<AuthorIdType>();
            npgsql.MapEnum<MessagePartType>();
            npgsql.MapEnum<MessageEncryptionState>();
            npgsql.MapEnum<MlsGenerationState>();
            npgsql.MapEnum<MlsJoinRequestState>();
        }).UseSnakeCaseNamingConvention();

        using var ctx = new NpgsqlOnlyContext(builder.Options);

        var sql = DmRetentionSweepService.NextUserPage(ctx, "user-1", 500).ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("ORDER BY"), "the page has to be ordered or it is not a page");
            Assert.That(sql, Does.Contain("LIMIT"));
            Assert.That(sql, Does.Contain("DISTINCT"));
        });
    }

    /// <summary>Real provider, no connection - see TheRotationPageTranslatesOnTheRealProvider.</summary>
    private sealed class NpgsqlOnlyContext(DbContextOptions<MicroserviceContext> options)
        : MicroserviceContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Provider already supplied; calling base would read the environment's connection string.
        }
    }

    [Test]
    public async Task AnInstanceWithNoMembersDoesNotCountRotations()
    {
        // Otherwise the rotation counter - the signal an operator watches to know the sweep is
        // keeping up - ticks up forever on a deployment that has nothing to sweep.
        var result = await Sweep(retentionDays: 30).SweepAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.RotationCompleted, Is.False);
            Assert.That(result.UsersExamined, Is.Zero);
        });
    }

    [Test]
    public async Task AUserWhoseSweepThrowsDoesNotStallTheRotation()
    {
        // Holding the cursor back on a failing account is the original defect wearing a different
        // hat: everybody behind it stops being swept. The failure is logged and the position moves.
        _context.Members.AddRange(
            MakeMember("m-1", "user-1", "conv-1"),
            MakeMember("m-2", "user-2", "conv-2"));
        await _context.SaveChangesAsync();

        var survivor = await SeedMessage("conv-2", "user-2", daysOld: 40);

        var log = new RecordingLogger<DmRetentionSweepService>();
        var options = new DmRetentionOptions { MaxUsersPerTick = 1 };
        var sweep = Sweep(
            retentionDays: 30, options, ["user-1", "user-2"],
            repo: new ThrowsForRepository("user-1", new EfCoreMessageRepository(_context)),
            logger: log);

        await sweep.SweepAsync(CancellationToken.None);   // user-1 throws
        await sweep.SweepAsync(CancellationToken.None);   // user-2 must still be reached

        var remaining = await _context.Messages.Select(m => m.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain(survivor.Id));
            Assert.That(log.Entries.Any(e => e.Level == LogLevel.Error), Is.True,
                "a failed account must be reported, not just stepped over");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Defect 2: the Scylla delete path ships disabled
    // ══════════════════════════════════════════════════════════════════════════

    private ScyllaMessageRepository ScyllaRepo(FakeCassandraMapper mapper) =>
        new(ScyllaContext.CreateDebug(mapper));

    [Test]
    public async Task WithTheScyllaGateClosed_NothingIsRead_AndNothingIsDeleted()
    {
        // The default.
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var mapper = new FakeCassandraMapper();
        var log = new RecordingLogger<DmRetentionSweepService>();
        var options = new DmRetentionOptions { ScyllaDeleteEnabled = false };

        var result = await Sweep(retentionDays: 30, options, repo: ScyllaRepo(mapper), logger: log)
            .SweepAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Skipped, Is.True);
            Assert.That(mapper.Fetches, Is.Empty);
            Assert.That(log.Any(LogLevel.Warning, "RETENTION_DM_SCYLLA_ENABLED"), Is.True,
                "a disabled delete path has to be visibly inert, not silently inert");
        });
    }

    [Test]
    public async Task WithTheScyllaGateClosed_TheCursorDoesNotMove()
    {
        // A skipped tick that still advanced the rotation would burn through everybody's turn
        // without sweeping them, so enabling the gate later would start from an arbitrary position
        // with a rotation counter that had been lying.
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        await Sweep(retentionDays: 30, new DmRetentionOptions { ScyllaDeleteEnabled = false },
                repo: ScyllaRepo(new FakeCassandraMapper()))
            .SweepAsync(CancellationToken.None);

        Assert.That(await ReadCursor(), Is.Null);
    }

    [Test]
    public async Task WithTheScyllaGateOpen_TheRangeReadIsActuallyIssued()
    {
        // The flag has to be the only thing standing between the sweep and the cluster - a gate that
        // cannot be opened is indistinguishable from a feature that was never wired up.
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var mapper = new FakeCassandraMapper();
        var options = new DmRetentionOptions { ScyllaDeleteEnabled = true };

        var result = await Sweep(retentionDays: 30, options, repo: ScyllaRepo(mapper))
            .SweepAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Skipped, Is.False);
            Assert.That(mapper.Fetches, Is.Not.Empty);
            Assert.That(mapper.Fetches[0].Cql, Does.Contain("FROM messages"));
        });
    }

    [Test]
    public async Task TheGateDoesNotApplyToTheRelationalBackend()
    {
        // The EF path's range read is ordinary SQL, exercised by every other test in this fixture.
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var old = await SeedMessage("conv-1", "user-1", daysOld: 40);

        var result = await Sweep(retentionDays: 30, new DmRetentionOptions { ScyllaDeleteEnabled = false })
            .SweepAsync(CancellationToken.None);

        var remaining = await _context.Messages.Select(m => m.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Skipped, Is.False);
            Assert.That(remaining, Does.Not.Contain(old.Id));
        });
    }

    /// <summary>Delegates everything except one user's range read, which throws - so "one account's
    /// failure does not stall the rotation" can be asserted without a broken store.</summary>
    private sealed class ThrowsForRepository(string userId, IMessageRepository inner) : IMessageRepository
    {
        public Task<IReadOnlyList<Message>> GetContextMessagesOlderThanAsync(
            string contextId, DateTimeOffset olderThan, DateTimeOffset afterCreatedAt, string afterMessageId, int limit)
            => contextId.EndsWith(userId[^1])
                ? throw new InvalidOperationException("store is down for this conversation")
                : inner.GetContextMessagesOlderThanAsync(contextId, olderThan, afterCreatedAt, afterMessageId, limit);

        public Task<Message> CreateMessageAsync(Message message) => inner.CreateMessageAsync(message);
        public Task<Message?> GetMessageAsync(string messageId) => inner.GetMessageAsync(messageId);
        public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByConversationIdAsync(string conversationId, int take, int skip)
            => inner.GetMessagesByConversationIdAsync(conversationId, take, skip);
        public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByContextIdAsync(string contextId, int take, int skip)
            => inner.GetMessagesByContextIdAsync(contextId, take, skip);
        public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagesByChannelIdAsync(string channelId, int take, int skip)
            => inner.GetMessagesByChannelIdAsync(channelId, take, skip);
        public Task<(ICollection<Message>, Dictionary<string, List<Reaction>>)> GetMessagePageByCursorAsync(MessagePageQuery query)
            => inner.GetMessagePageByCursorAsync(query);
        public Task<Message> UpdateMessageAsync(Message message) => inner.UpdateMessageAsync(message);
        public Task DeleteMessageAsync(Message message) => inner.DeleteMessageAsync(message);
        public Task DeleteMessagesAsync(IReadOnlyCollection<Message> messages) => inner.DeleteMessagesAsync(messages);
        public Task<Message> PinMessageAsync(Message message, string pinnedById) => inner.PinMessageAsync(message, pinnedById);
        public Task<Message> UnpinMessageAsync(Message message) => inner.UnpinMessageAsync(message);
        public Task<ICollection<Message>> GetPinnedMessagesAsync(string contextId, int limit = 50) => inner.GetPinnedMessagesAsync(contextId, limit);
        public Task AddReactionAsync(Reaction reaction) => inner.AddReactionAsync(reaction);
        public Task RemoveReactionAsync(string contextId, string messageId, string emoji, string userId)
            => inner.RemoveReactionAsync(contextId, messageId, emoji, userId);
    }
}
