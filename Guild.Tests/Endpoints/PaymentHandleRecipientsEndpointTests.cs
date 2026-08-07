using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The recipients route: who a client seals payment handles to, and with which key.
/// </summary>
[TestFixture]
public class PaymentHandleRecipientsEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string AnnaId = "user-anna";
    private const string BenId = "user-ben";
    private const string OutsiderId = "user-outsider";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissions = null!;
    private LedgerService _ledger = null!;
    private FakeInvokingMessageBus _bus = null!;
    private PaymentHandleEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _ledger = new LedgerService(_context);
        _bus = new FakeInvokingMessageBus();
        _endpoint = new PaymentHandleEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task SeedGuildAsync(GuildFeatures features = GuildFeatures.Ledger,
        params string[] memberUserIds)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "The Flat", Features = features,
            Kind = GuildKind.Household,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var userId in memberUserIds)
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"member-{userId}", GuildId = GuildId, UserId = userId,
                JoinedAt = DateTime.UtcNow, SearchValue = $"{userId}#{GuildId}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    private static UserDeviceKeyDto Device(string userId, string deviceId, byte[] publicKey,
        bool hasValidCertificate = true, DateTimeOffset? certificateRevokedAt = null,
        bool isActive = true) => new()
    {
        UserId = userId,
        DeviceId = deviceId,
        DeviceName = deviceId,
        PublicKey = publicKey,
        HasValidCertificate = hasValidCertificate,
        CertificateRevokedAt = certificateRevokedAt,
        IsActive = isActive,
    };

    private void IdentityAnswers(params UserDeviceKeyDto[] devices) =>
        _bus.SetResponse<GetUserDeviceKeysRequest>(
            new GetUserDeviceKeysResponse { Devices = devices.ToList() });

    private void IdentityAnswers(GetUserDeviceKeysResponse response) =>
        _bus.SetResponse<GetUserDeviceKeysRequest>(response);

    private Task<IResult> Recipients(string userId) =>
        _endpoint.RecipientsAsync(GuildId, _permissions, _ledger, _bus, TestPrincipal.Create(userId));

    private static PaymentHandleRecipientsDto Body(IResult result) =>
        ((Ok<PaymentHandleRecipientsDto>)result).Value!;

    // ══════════════════════════════════════════════════════════════════════════ The key, which is
    // the whole point ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Recipients_ReturnsThePublicKeyForEachDevice()
    {
        // Without this the feature does not work at all: a client that cannot get a recipient's
        // public key cannot seal to it, so every sealed blob would have exactly one wrap - its own.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswers(
            Device(AnnaId, "anna-phone", [0x01, 0x02]),
            Device(BenId, "ben-laptop", [0x03, 0x04]));

        var body = Body(await Recipients(AnnaId));

        Assert.That(body.Recipients.Select(r => (r.DeviceId, r.PublicKey)),
            Is.EquivalentTo(new[]
            {
                ("anna-phone", new byte[] { 0x01, 0x02 }),
                ("ben-laptop", new byte[] { 0x03, 0x04 }),
            }));
    }

    [Test]
    public async Task Recipients_AsksIdentityForTheNonConsumingDirectory()
    {
        // Not ConsumeMlsDeviceTokensForUserRequest, which stamps a single-use key package consumed
        // per device per call.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);
        IdentityAnswers(Device(AnnaId, "anna-phone", [0x01]));

        await Recipients(AnnaId);

        Assert.That(_bus.Invoked.Single(), Is.InstanceOf<GetUserDeviceKeysRequest>());
    }

    [Test]
    public async Task Recipients_RosterVersionMatchesTheMemberListItWasBuiltFrom()
    {
        // Derived from the same read the recipients came from, so a client cannot store a version
        // describing a roster it never saw.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswers(Device(AnnaId, "anna-phone", [0x01]));

        var body = Body(await Recipients(AnnaId));

        Assert.That(body.MemberRosterVersion,
            Is.EqualTo(LedgerService.ComputeRosterVersion([AnnaId, BenId])));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Scoping: members of this guild, never a bare user-id lookup
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Recipients_AsksOnlyAboutMembersOfThisGuild()
    {
        // The request itself is the scope.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswers(Device(AnnaId, "anna-phone", [0x01]));

        await Recipients(AnnaId);

        var request = (GetUserDeviceKeysRequest)_bus.Invoked.Single();
        Assert.That(request.UserIds, Is.EquivalentTo(new[] { AnnaId, BenId }));
    }

    [Test]
    public async Task Recipients_NonMemberDeviceInTheAnswerIsDropped()
    {
        // Identity is only ever asked about members, so this should be unreachable.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswers(
            Device(AnnaId, "anna-phone", [0x01]),
            Device(OutsiderId, "outsider-phone", [0x99]));

        var body = Body(await Recipients(AnnaId));

        Assert.That(body.Recipients.Select(r => r.UserId), Is.EquivalentTo(new[] { AnnaId }));
    }

    [Test]
    public async Task Recipients_MemberWithNoDevices_IsSimplyMissingFromTheList()
    {
        // Ben has registered nothing yet.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswers(Device(AnnaId, "anna-phone", [0x01]));

        var body = Body(await Recipients(AnnaId));

        Assert.Multiple(() =>
        {
            Assert.That(body.Recipients, Has.Count.EqualTo(1));
            Assert.That(body.UnresolvedMemberIds, Is.Empty);
        });
    }

    [Test]
    public async Task Recipients_NoDevicesAtAll_IsAnEmptyListNotAnError()
    {
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);
        IdentityAnswers();

        var body = Body(await Recipients(AnnaId));

        Assert.That(body.Recipients, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Flags survive the
    // relay ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Recipients_RevokedAndUncertifiedDevices_AreFlaggedNotFiltered()
    {
        // Guild is not the right place to decide that a housemate should not see one of Ben's
        // devices.
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-2);
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswers(
            Device(BenId, "ben-laptop", [0x03]),
            Device(BenId, "ben-old-phone", [0x04], hasValidCertificate: false, certificateRevokedAt: revokedAt),
            Device(BenId, "ben-tablet", [0x05], hasValidCertificate: false, isActive: false));

        var body = Body(await Recipients(AnnaId));

        var revoked = body.Recipients.Single(r => r.DeviceId == "ben-old-phone");
        var removed = body.Recipients.Single(r => r.DeviceId == "ben-tablet");
        Assert.Multiple(() =>
        {
            Assert.That(body.Recipients, Has.Count.EqualTo(3), "All three are still listed");
            Assert.That(revoked.CertificateRevokedAt, Is.EqualTo(revokedAt));
            Assert.That(revoked.HasValidCertificate, Is.False);
            Assert.That(revoked.PublicKey, Is.EqualTo(new byte[] { 0x04 }),
                "Still carries its key - declining to seal to it is the client's decision");
            Assert.That(removed.IsActive, Is.False);
        });
    }

    [Test]
    public async Task Recipients_TruncatedIdentityBatch_IsReportedNotSwallowed()
    {
        // A roster over Identity's batch cap comes back short.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswers(new GetUserDeviceKeysResponse
        {
            Devices = [Device(AnnaId, "anna-phone", [0x01])],
            OmittedUserIds = [BenId],
        });

        var body = Body(await Recipients(AnnaId));

        Assert.That(body.UnresolvedMemberIds, Is.EquivalentTo(new[] { BenId }));
    }

    // ══════════════════════════════════════════════════════════════════════════ The gate,
    // unchanged ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Recipients_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);

        var result = await _endpoint.RecipientsAsync(GuildId, _permissions, _ledger, _bus,
            TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Recipients_LedgerFeatureDisabled_ReturnsForbid()
    {
        await SeedGuildAsync(GuildFeatures.None, AnnaId);

        Assert.Multiple(async () =>
        {
            Assert.That(await Recipients(AnnaId), Is.InstanceOf<ForbidHttpResult>());
            Assert.That(_bus.Invoked, Is.Empty, "The bus must not be reached before the gate passes");
        });
    }

    [Test]
    public async Task Recipients_CallerIsNotAMember_ReturnsForbid()
    {
        // The device roster of a household is not public.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);

        Assert.Multiple(async () =>
        {
            Assert.That(await Recipients(OutsiderId), Is.InstanceOf<ForbidHttpResult>());
            Assert.That(_bus.Invoked, Is.Empty);
        });
    }
}
