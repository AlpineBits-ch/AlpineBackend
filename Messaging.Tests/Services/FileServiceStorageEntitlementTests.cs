using System.Reflection;
using System.Reflection.Emit;
using Echo.Entitlements;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Messaging.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Messaging.Tests.Services;

/// <summary>
/// The storage choke point: <c>storage.upload_max_bytes</c> (paired), <c>user.upload_max_bytes</c>
/// (no guild to pair against) and <c>storage.guild_quota_bytes</c> (running usage), plus the
/// operator ceiling that clamps below all three.
/// </summary>
[TestFixture]
public class FileServiceStorageEntitlementTests
{
    private const long Mb = 1024L * 1024L;
    private const string GuildId = "guild-1";
    private const string UserId = "user-1";

    private RecordingObjectStore _objects = null!;
    private MemoryGuildStorageLedger _ledger = null!;

    [SetUp]
    public void SetUp()
    {
        _objects = new RecordingObjectStore();
        _ledger = new MemoryGuildStorageLedger();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // user.upload_max_bytes - the no-guild path
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UserUpload_ExactlyAtTheCeiling_IsStored()
    {
        var service = Service(user: [(EntitlementKeys.UserUploadMaxBytes, 25 * Mb)]);

        var result = await service.UploadFileAsync(
            [Sized("holiday.png", 25 * Mb)], StorageUploadContext.ForUser(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Uploaded, Has.Count.EqualTo(1), "a request for exactly the ceiling got what it asked for");
            Assert.That(result.Rejected, Is.Empty, "an equal request is not a degradation");
        });
    }

    [Test]
    public async Task UserUpload_OneByteOverTheCeiling_IsRefusedWithAReason()
    {
        var service = Service(user: [(EntitlementKeys.UserUploadMaxBytes, 25 * Mb)]);

        var result = await service.UploadFileAsync(
            [Sized("holiday.png", 25 * Mb + 1)], StorageUploadContext.ForUser(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.True);
            Assert.That(result.Rejected[0].Code, Is.EqualTo(StorageRefusalCode.UploadTooLarge));
            Assert.That(result.Rejected[0].Key.Name, Is.EqualTo("user.upload_max_bytes"),
                "a DM upload has no guild side, so it must not resolve the paired key");
            Assert.That(result.Rejected[0].Cause, Is.EqualTo(EntitlementDegradationReason.UserPlanLimit));
            Assert.That(result.Rejected[0].BoundBy, Is.EqualTo(EntitlementBoundBy.User));
            Assert.That(_objects.Keys, Is.Empty);
        });
    }

    [Test]
    public async Task UserUpload_FarOverTheCeiling_IsRefusedTheSameWay()
    {
        var service = Service(user: [(EntitlementKeys.UserUploadMaxBytes, 25 * Mb)]);

        var result = await service.UploadFileAsync(
            [Sized("raw.mov", 4096 * Mb)], StorageUploadContext.ForUser(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.True);
            Assert.That(result.Rejected[0].Code, Is.EqualTo(StorageRefusalCode.UploadTooLarge));
            Assert.That(result.Rejected[0].LimitBytes, Is.EqualTo(25 * Mb));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // storage.upload_max_bytes - the paired key, in both directions
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PairedCeiling_PlusMemberInAFreeGuild_GetsTheGuildsLowerCeiling()
    {
        var service = Service(
            guild: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)],
            user: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)]);

        var limits = await service.LimitsForAsync(StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(limits.UploadCeilingBytes, Is.EqualTo(25 * Mb),
                "the guild's plan caps what the platform will store, whatever the member pays");
            Assert.That(limits.UploadKey.Name, Is.EqualTo("storage.upload_max_bytes"));
            Assert.That(limits.UploadCause, Is.EqualTo(EntitlementDegradationReason.PairedCeiling));
        });
    }

    [Test]
    public async Task PairedCeiling_FreeMemberInAProGuild_GetsTheirOwnLowerCeiling()
    {
        var service = Service(
            guild: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)],
            user: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)]);

        var limits = await service.LimitsForAsync(StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(limits.UploadCeilingBytes, Is.EqualTo(25 * Mb),
                "reversing the min here would let one paying member push 500 MB into a guild that pays nothing");
            Assert.That(limits.UploadCause, Is.EqualTo(EntitlementDegradationReason.PairedCeiling));
        });
    }

    [Test]
    public async Task PairedCeiling_BothSidesAgree_IsCreditedToTheGuild()
    {
        var service = Service(
            guild: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)],
            user: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)]);

        var limits = await service.LimitsForAsync(StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.That(limits.UploadCause, Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit),
            "when the two sides do not disagree there is nothing paired about the answer, and the "
            + "upgrade surface to show is the guild's");
    }

    [Test]
    public async Task PairedCeiling_AtOverAndFarOver()
    {
        var service = Service(
            guild: [(EntitlementKeys.StorageUploadMaxBytes, 100 * Mb)],
            user: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)]);

        var context = StorageUploadContext.ForGuild(GuildId, UserId);

        var atLimit = await service.UploadFileAsync([Sized("a.bin", 25 * Mb)], context);
        var justOver = await service.UploadFileAsync([Sized("b.bin", 25 * Mb + 1)], context);
        var farOver = await service.UploadFileAsync([Sized("c.bin", 8192 * Mb)], context);

        Assert.Multiple(() =>
        {
            Assert.That(atLimit.Uploaded, Has.Count.EqualTo(1));
            Assert.That(justOver.Refused, Is.True);
            Assert.That(farOver.Refused, Is.True);
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(25 * Mb),
                "only the file that was actually stored counts against the quota");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // storage.guild_quota_bytes - freeze, never delete
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Quota_UploadThatLandsExactlyOnTheLimit_IsStored()
    {
        _ledger.Seed(GuildId, 90 * Mb);
        var service = QuotaService(100 * Mb);

        var result = await service.UploadFileAsync(
            [Sized("last.png", 10 * Mb)], StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Uploaded, Has.Count.EqualTo(1));
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(100 * Mb));
        });
    }

    [Test]
    public async Task Quota_UploadThatWouldCrossTheLimitByOneByte_IsFrozen()
    {
        _ledger.Seed(GuildId, 90 * Mb);
        var service = QuotaService(100 * Mb);

        var result = await service.UploadFileAsync(
            [Sized("one-too-many.png", 10 * Mb + 1)], StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.True);
            Assert.That(result.Rejected[0].Code, Is.EqualTo(StorageRefusalCode.GuildQuotaExhausted));
            Assert.That(result.Rejected[0].Key.Name, Is.EqualTo("storage.guild_quota_bytes"));
            Assert.That(result.Rejected[0].Cause, Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit));
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(90 * Mb), "a frozen upload consumes nothing");
            Assert.That(_objects.Keys, Is.Empty, "and stores nothing");
        });
    }

    [Test]
    public async Task Quota_GuildFarPastItsLimitAfterADowngrade_KeepsEverythingAndOnlyFreezesGrowth()
    {
        // The downgrade case from docs/legal/downgrade-2026-08-14.md section 4.2: a guild that was
        // on a larger plan is now holding ten times what its plan allows.
        _ledger.Seed(GuildId, 1000 * Mb);
        var service = QuotaService(100 * Mb);

        var result = await service.UploadFileAsync(
            [Sized("anything.png", 1)], StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.True);
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(1000 * Mb),
                "being over the limit must never reduce what the guild is holding");
        });
    }

    [Test]
    public async Task Quota_IsConsumedAcrossABatchRatherThanPerFile()
    {
        _ledger.Seed(GuildId, 70 * Mb);
        var service = QuotaService(100 * Mb);

        var result = await service.UploadFileAsync(
            [Sized("a.png", 20 * Mb), Sized("b.png", 20 * Mb)],
            StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Uploaded, Has.Count.EqualTo(1), "each file on its own fits; together they do not");
            Assert.That(result.Rejected, Has.Count.EqualTo(1));
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(90 * Mb));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Accounting
    // integrity ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Quota_UploadThatFailsInTheObjectStore_ConsumesNothingForTheFileThatFailed()
    {
        var service = QuotaService(1000 * Mb, failAfter: 1);

        Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadFileAsync(
            [Sized("first.png", 10 * Mb), Sized("second.png", 10 * Mb)],
            StorageUploadContext.ForGuild(GuildId, UserId)));

        Assert.Multiple(() =>
        {
            Assert.That(_objects.Keys, Has.Count.EqualTo(1));
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(10 * Mb),
                "usage is credited after the object is in the bucket, so a put that threw is not billed");
        });
    }

    [Test]
    public async Task Quota_ReleasingBytesUnfreezesTheGuild()
    {
        _ledger.Seed(GuildId, 100 * Mb);
        var service = QuotaService(100 * Mb);
        var context = StorageUploadContext.ForGuild(GuildId, UserId);

        var whileFull = await service.UploadFileAsync([Sized("a.png", 5 * Mb)], context);

        // What a delete performed elsewhere calls.
        await service.ReleaseAsync(GuildId, 20 * Mb);

        var afterRelease = await service.UploadFileAsync([Sized("a.png", 5 * Mb)], context);

        Assert.Multiple(() =>
        {
            Assert.That(whileFull.Refused, Is.True);
            Assert.That(afterRelease.Uploaded, Has.Count.EqualTo(1));
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(85 * Mb));
        });
    }

    [Test]
    public void Release_RefusesANegativeSize()
    {
        var service = QuotaService(100 * Mb);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReleaseAsync(GuildId, -1));
    }

    [Test]
    public async Task Quota_WithNoLedgerRegistered_NeverAccumulates()
    {
        var service = new FileService(
            null!,
            Resolver(
                guild:
                [
                    (EntitlementKeys.StorageGuildQuotaBytes, 10 * Mb),
                    (EntitlementKeys.StorageUploadMaxBytes, 100 * Mb),
                ],
                user: [(EntitlementKeys.StorageUploadMaxBytes, 100 * Mb)]),
            objectStore: _objects);

        var context = StorageUploadContext.ForGuild(GuildId, UserId);
        var first = await service.UploadFileAsync([Sized("a.png", 6 * Mb)], context);
        var second = await service.UploadFileAsync([Sized("b.png", 6 * Mb)], context);

        Assert.Multiple(() =>
        {
            Assert.That(first.Uploaded, Has.Count.EqualTo(1));
            Assert.That(second.Uploaded, Has.Count.EqualTo(1),
                "a service that came up without its accounting store sees every guild as holding "
                + "nothing, and must not refuse uploads on the strength of a number it does not have");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Degradation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PartialBatch_StoresWhatFitsAndReportsTheReduction()
    {
        var service = Service(
            guild: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)],
            user: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)]);

        var result = await service.UploadFileAsync(
            [Sized("a.png", 10 * Mb), Sized("huge.mov", 400 * Mb), Sized("c.png", 5 * Mb)],
            StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Uploaded.Select(f => f.FileName), Is.EqualTo(new[] { "a.png", "c.png" }),
                "one oversized file used to refuse the whole batch");
            Assert.That(result.Refused, Is.False);
            Assert.That(result.Rejected, Has.Count.EqualTo(1));

            // One degradation per rejected file, not one for the batch.
            var degradation = result.Rejected[0].Degradation;
            Assert.That(degradation.Key, Is.EqualTo("storage.upload_max_bytes"));
            Assert.That(degradation.Requested, Is.EqualTo((400 * Mb).ToString()));
            Assert.That(degradation.Granted, Is.EqualTo((25 * Mb).ToString()));
            Assert.That(degradation.Reason, Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit));
        });
    }

    [Test]
    public async Task FullBatch_ReportsNoDegradation()
    {
        var service = Service(
            guild: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)],
            user: [(EntitlementKeys.StorageUploadMaxBytes, 25 * Mb)]);

        var result = await service.UploadFileAsync(
            [Sized("a.png", 1 * Mb), Sized("b.png", 1 * Mb)],
            StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.That(result.Rejected, Is.Empty);
    }

    [Test]
    public async Task EmptyRequest_IsNeitherRefusedNorDegraded()
    {
        var result = await QuotaService(1).UploadFileAsync(
            [], StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.False);
            Assert.That(result.Uploaded, Is.Empty);
            Assert.That(result.Rejected, Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Operator ceilings and the instance default
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task OperatorCeilingBelowTheEntitlement_Wins_AndSaysSo()
    {
        var service = new FileService(
            null!,
            Resolver(guild: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)],
                user: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)]),
            Ceilings(10 * Mb), _ledger, _objects);

        var limits = await service.LimitsForAsync(StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(limits.UploadCeilingBytes, Is.EqualTo(10 * Mb));
            Assert.That(limits.UploadCause, Is.EqualTo(EntitlementDegradationReason.OperatorCeiling),
                "no upgrade lifts a limit this instance's operator set, so the client must not be "
                + "shown an upgrade link for it");
        });
    }

    [Test]
    public async Task OperatorCeilingAboveTheEntitlement_DoesNotRaiseIt()
    {
        var service = new FileService(
            null!,
            Resolver(guild: [(EntitlementKeys.StorageUploadMaxBytes, 10 * Mb)],
                user: [(EntitlementKeys.StorageUploadMaxBytes, 10 * Mb)]),
            Ceilings(500 * Mb), _ledger, _objects);

        var limits = await service.LimitsForAsync(StorageUploadContext.ForGuild(GuildId, UserId));

        Assert.Multiple(() =>
        {
            Assert.That(limits.UploadCeilingBytes, Is.EqualTo(10 * Mb),
                "an env var is not a way to buy a bigger plan");
            Assert.That(limits.UploadCause, Is.EqualTo(EntitlementDegradationReason.GuildPlanLimit));
        });
    }

    [Test]
    public async Task WithNothingConfiguredAtAll_TodaysThirtyFiveMegabyteCeilingStillApplies()
    {
        var service = new FileService(null!, objectStore: _objects, storageLedger: _ledger);

        var atLimit = await service.UploadFileAsync(
            [Sized("a.bin", FileService.InstanceDefaultUploadCeilingBytes)],
            StorageUploadContext.ForUser(UserId));
        var justOver = await service.UploadFileAsync(
            [Sized("b.bin", FileService.InstanceDefaultUploadCeilingBytes + 1)],
            StorageUploadContext.ForUser(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(FileService.InstanceDefaultUploadCeilingBytes, Is.EqualTo(35 * Mb));
            Assert.That(atLimit.Uploaded, Has.Count.EqualTo(1));
            Assert.That(justOver.Refused, Is.True,
                "removing today's only cap while replacing it with one that ships unconfigured would "
                + "have made uploads unbounded on every existing instance");
            Assert.That(justOver.Rejected[0].Cause, Is.EqualTo(EntitlementDegradationReason.OperatorCeiling));
        });
    }

    [Test]
    public async Task AConfiguredPlanBeatsTheInstanceDefaultInBothDirections()
    {
        var larger = Service(user: [(EntitlementKeys.UserUploadMaxBytes, 500 * Mb)]);
        var smaller = Service(user: [(EntitlementKeys.UserUploadMaxBytes, 1 * Mb)]);

        var largerLimits = await larger.LimitsForAsync(StorageUploadContext.ForUser(UserId));
        var smallerLimits = await smaller.LimitsForAsync(StorageUploadContext.ForUser(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(largerLimits.UploadCeilingBytes, Is.EqualTo(500 * Mb));
            Assert.That(smallerLimits.UploadCeilingBytes, Is.EqualTo(1 * Mb));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ The wire
    // vocabulary ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every rejection this service can produce has to survive <see
    /// cref="EntitlementDegradationDto"/>'s constructor, which validates.
    /// </summary>
    [Test]
    public async Task EveryRejectionConvertsToTheWireContract()
    {
        var cases = new[]
        {
            // Paired, guild side lower.
            (Service(guild: [(EntitlementKeys.StorageUploadMaxBytes, 1 * Mb)],
                    user: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)]),
                StorageUploadContext.ForGuild(GuildId, UserId)),

            // Paired, user side lower.
            (Service(guild: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)],
                    user: [(EntitlementKeys.StorageUploadMaxBytes, 1 * Mb)]),
                StorageUploadContext.ForGuild(GuildId, UserId)),

            // User only, no guild to pair against.
            (Service(user: [(EntitlementKeys.UserUploadMaxBytes, 1 * Mb)]),
                StorageUploadContext.ForUser(UserId)),

            // Operator ceiling, which must carry no remedy and no side.
            (new FileService(
                    null!,
                    Resolver(guild: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)],
                        user: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)]),
                    Ceilings(1 * Mb), _ledger, _objects),
                StorageUploadContext.ForGuild(GuildId, UserId)),

            // The quota, whose degradation is a total rather than a file size.
            (QuotaService(1 * Mb), StorageUploadContext.ForGuild(GuildId, UserId)),
        };

        foreach (var (service, context) in cases)
        {
            var result = await service.UploadFileAsync([Sized("big.bin", 50 * Mb)], context);
            var rejection = result.Rejected.Single();

            var subject = rejection.BoundBy == EntitlementBoundBy.User || context.GuildId is null
                ? EntitlementSubject.ForUser(UserId)
                : EntitlementSubject.ForGuild(GuildId);

            var remedy = EntitlementRemedyPolicy.For(
                rejection.Cause, rejection.BoundBy, instanceSellsUpgrades: true, actorCanManageGuild: true);

            var degradation = EntitlementDegradationDto.From(
                rejection.Degradation, rejection.Key, subject, remedy, rejection.BoundBy);

            var denial = EntitlementDenialDto.From(degradation);

            Assert.Multiple(() =>
            {
                Assert.That(EntitlementReasonCodes.IsKnown(degradation.Reason), Is.True);
                Assert.That(degradation.Granted.Kind, Is.EqualTo(EntitlementValueDto.NumericKind));
                Assert.That(degradation.Granted.Unlimited, Is.False,
                    "a limit that bound is by definition not unlimited");
                Assert.That(denial.Code, Is.EqualTo(denial.Reason));
                Assert.That(denial.Retryable, Is.False);
            });
        }
    }

    [Test]
    public async Task AnOperatorCeilingCarriesNoSideAndNoRemedy()
    {
        var service = new FileService(
            null!,
            Resolver(guild: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)],
                user: [(EntitlementKeys.StorageUploadMaxBytes, 500 * Mb)]),
            Ceilings(1 * Mb), _ledger, _objects);

        var result = await service.UploadFileAsync(
            [Sized("big.bin", 50 * Mb)], StorageUploadContext.ForGuild(GuildId, UserId));

        var rejection = result.Rejected.Single();
        var remedy = EntitlementRemedyPolicy.For(
            rejection.Cause, rejection.BoundBy, instanceSellsUpgrades: true, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(rejection.Cause, Is.EqualTo(EntitlementDegradationReason.OperatorCeiling));
            Assert.That(rejection.BoundBy, Is.Null, "an operator ceiling is not a subject anybody upgrades");
            Assert.That(remedy.Remedy, Is.EqualTo(EntitlementRemedyCodes.None));
            Assert.That(remedy.ActorCanRemedy, Is.False,
                "an upgrade button here sells a change that would not happen");
        });
    }

    /// <summary>The consumption half of the client's meter.</summary>
    [Test]
    public async Task GuildUsageIsExposedInTheShapeTheMeterReads()
    {
        _ledger.Seed(GuildId, 3 * Mb);
        var service = QuotaService(100 * Mb);

        var usage = await service.UsageForGuildAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(usage.Subject.Kind, Is.EqualTo("guild"));
            Assert.That(usage.Subject.Id, Is.EqualTo(GuildId));
            Assert.That(usage.Used["storage.guild_quota_bytes"], Is.EqualTo(3 * Mb));
        });
    }

    /// <summary>
    /// The container has to be able to build this service from exactly what <c>Program.cs</c>
    /// registers.
    /// </summary>
    [Test]
    public void TheContainerCanBuildTheServiceFromWhatMessagingRegisters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Amazon.S3.IAmazonS3>(_ => null!);
        services.AddEntitlements();
        services.AddLicenseMode(LicenseMode.SelfHost);
        services.AddSingleton<IGuildStorageLedger, UnmeteredGuildStorageLedger>();
        services.AddScoped<FileService>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.That(scope.ServiceProvider.GetRequiredService<FileService>(), Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════ The commitment
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the IL of every method in the storage enforcement path and fails if any of them calls
    /// anything that deletes.
    /// </summary>
    [Test]
    public void NoCodePathInStorageEnforcementDeletesAnything()
    {
        Type[] enforcement =
        [
            typeof(FileService),
            typeof(S3AttachmentObjectStore),
            typeof(RedisGuildStorageLedger),
            typeof(UnmeteredGuildStorageLedger),
        ];

        var offenders = enforcement
            .SelectMany(WithNestedTypes)
            .SelectMany(CalledMethods)
            .Where(IsDeletion)
            .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .Distinct()
            .ToList();

        Assert.That(offenders, Is.Empty,
            "over quota freezes new uploads and never removes anything; these calls would break that");
    }

    [Test]
    public void TheObjectStoreSeamCannotExpressADelete()
    {
        var members = typeof(IAttachmentObjectStore).GetMembers().Select(m => m.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(members, Has.None.Contains("Delete"));
            Assert.That(members, Has.None.Contains("Remove"));
            Assert.That(typeof(IGuildStorageLedger).GetMembers().Select(m => m.Name),
                Has.None.Contains("Delete"),
                "the ledger counts bytes; it does not own them and must not be able to remove any");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Fixtures
    // ══════════════════════════════════════════════════════════════════════════

    private FileService Service(
        (EntitlementKey Key, long Value)[]? guild = null,
        (EntitlementKey Key, long Value)[]? user = null) =>
        new(null!, Resolver(guild, user), OperatorCeilings.None, _ledger, _objects);

    /// <summary>A guild with a real quota and a deliberately generous upload ceiling, so that the
    /// quota is unambiguously the limit that bound.</summary>
    private FileService QuotaService(long quotaBytes, int failAfter = int.MaxValue)
    {
        _objects.FailAfter = failAfter;
        return new FileService(
            null!,
            Resolver(
                guild:
                [
                    (EntitlementKeys.StorageGuildQuotaBytes, quotaBytes),
                    (EntitlementKeys.StorageUploadMaxBytes, 100_000 * Mb),
                ],
                user: [(EntitlementKeys.StorageUploadMaxBytes, 100_000 * Mb)]),
            OperatorCeilings.None, _ledger, _objects);
    }

    private static EntitlementResolver Resolver(
        (EntitlementKey Key, long Value)[]? guild = null,
        (EntitlementKey Key, long Value)[]? user = null) =>
        new([new StubSource(Build(guild), Build(user))]);

    private static EntitlementSet Build((EntitlementKey Key, long Value)[]? values)
    {
        if (values is null) return EntitlementSet.Empty;

        var builder = new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault);
        foreach (var (key, value) in values) builder.Number(key, value, "test-plan");
        return builder.Build();
    }

    private static OperatorCeilings Ceilings(long uploadMaxBytes) =>
        OperatorCeilings.Parse(new Dictionary<string, string?>
        {
            ["storage.upload_max_bytes"] = uploadMaxBytes.ToString(),
        });

    private static IFormFile Sized(string name, long length) => new FakeFormFile(name, length);

    private sealed class StubSource(EntitlementSet guild, EntitlementSet user) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.PlanDefault;

        public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult(subject.Kind == SubjectKind.Guild ? guild : user);
    }

    private sealed class MemoryGuildStorageLedger : IGuildStorageLedger
    {
        private readonly Dictionary<string, long> _used = new(StringComparer.Ordinal);

        public long Used(string guildId) => _used.GetValueOrDefault(guildId);

        public void Seed(string guildId, long bytes) => _used[guildId] = bytes;

        public Task<long> GetUsedBytesAsync(string guildId, CancellationToken ct) =>
            Task.FromResult(Used(guildId));

        public Task<long> AddAsync(string guildId, long deltaBytes, CancellationToken ct)
        {
            var total = Math.Max(0, Used(guildId) + deltaBytes);
            _used[guildId] = total;
            return Task.FromResult(total);
        }
    }

    private sealed class RecordingObjectStore : IAttachmentObjectStore
    {
        public List<string> Keys { get; } = [];

        /// <summary>How many puts succeed before the store starts throwing.</summary>
        public int FailAfter { get; set; } = int.MaxValue;

        public Task PutAsync(
            string bucketName, string key, string? contentType, Stream content, CancellationToken ct)
        {
            if (Keys.Count >= FailAfter) throw new InvalidOperationException("object store unavailable");

            Keys.Add(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>Carries a declared length without allocating it.</summary>
    private sealed class FakeFormFile(string fileName, long length) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => length;
        public string Name => "files";
        public string FileName => fileName;

        public void CopyTo(Stream target) => OpenReadStream().CopyTo(target);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            OpenReadStream().CopyToAsync(target, cancellationToken);

        public Stream OpenReadStream() => new MemoryStream([1, 2, 3, 4]);
    }

    // ── IL inspection ────────────────────────────────────────────────────────

    private static readonly Dictionary<short, OpCode> AllOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    /// <summary>A type and every type nested in it.</summary>
    private static IEnumerable<Type> WithNestedTypes(Type type)
    {
        yield return type;

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        foreach (var inner in WithNestedTypes(nested))
        {
            yield return inner;
        }
    }

    private static IEnumerable<MethodBase> CalledMethods(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var declared = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));

        foreach (var method in declared)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;

            foreach (var token in MethodTokens(il))
            {
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(
                        token,
                        type.IsGenericType ? type.GetGenericArguments() : null,
                        method.IsGenericMethodDefinition ? method.GetGenericArguments() : null);
                }
                catch (Exception)
                {
                    // A token this walker cannot resolve is a token it also cannot judge.
                }

                if (called is not null) yield return called;
            }
        }
    }

    private static IEnumerable<int> MethodTokens(byte[] il)
    {
        var offset = 0;

        while (offset < il.Length)
        {
            short value = il[offset];
            if (il[offset] == 0xFE && offset + 1 < il.Length)
            {
                value = (short)(0xFE00 | il[offset + 1]);
                offset += 2;
            }
            else
            {
                offset += 1;
            }

            if (!AllOpCodes.TryGetValue(value, out var opCode)) yield break;

            if (opCode.OperandType == OperandType.InlineSwitch)
            {
                var cases = BitConverter.ToInt32(il, offset);
                offset += 4 + (4 * cases);
                continue;
            }

            if (opCode.OperandType is OperandType.InlineMethod && offset + 4 <= il.Length)
            {
                yield return BitConverter.ToInt32(il, offset);
            }

            offset += OperandSize(opCode.OperandType);
        }
    }

    private static int OperandSize(OperandType operand) => operand switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4,
    };

    /// <summary>Whether a called method is one that removes stored data.</summary>
    private static bool IsDeletion(MethodBase method)
    {
        if (method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)) return true;

        var declaring = method.DeclaringType?.Namespace ?? string.Empty;

        return method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            && (declaring.StartsWith("Amazon.S3", StringComparison.Ordinal)
                || declaring.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }
}
