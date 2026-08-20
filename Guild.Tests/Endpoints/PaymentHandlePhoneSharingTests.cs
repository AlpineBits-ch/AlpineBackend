using Echo.Realtime.Devices;
using Guild.Application.Dtos.Request;
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

/// <summary>The per-guild phone-number opt-in, and the read path it gates.</summary>
[TestFixture]
public class PaymentHandlePhoneSharingTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string AnnaId = "user-anna";
    private const string BenId = "user-ben";
    private const string CaraId = "user-cara";
    private const string OutsiderId = "user-outsider";
    private const string AnnaDeviceId = "anna-phone";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissions = null!;
    private LedgerService _ledger = null!;
    private PaymentHandleService _handles = null!;
    private DeviceIdResolver _devices = null!;
    private FakeInvokingMessageBus _bus = null!;
    private PaymentHandleEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissions = PermissionTestFactory.Create(_cache, _context);
        _ledger = new LedgerService(_context);
        _handles = new PaymentHandleService(_context);
        _bus = new FakeInvokingMessageBus();
        _devices = new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance);
        _endpoint = new PaymentHandleEndpoint();

        // The directory route resolves the calling device before it will hand back anything, so
        // every read here needs Identity to confirm the header names a real device.
        _bus.SetResponse<ValidateUserDeviceRequest>(new ValidateUserDeviceResponse { IsRegistered = true });
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

    private void IdentityAnswersWithNumbers(params (string UserId, string Number)[] numbers) =>
        _bus.SetResponse<GetUserPhoneNumbersRequest>(new GetUserPhoneNumbersResponse
        {
            PhoneNumbers = numbers
                .Select(n => new UserPhoneNumberDto
                {
                    UserId = n.UserId,
                    PhoneNumber = n.Number,
                    UpdatedAt = DateTimeOffset.UtcNow,
                })
                .ToList(),
        });

    private static HttpContext HttpWithDevice(string? deviceId = AnnaDeviceId)
    {
        var http = new DefaultHttpContext();
        if (deviceId is not null) http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;
        return http;
    }

    private Task<IResult> Directory(string userId, HttpContext? http = null) =>
        _endpoint.GetAsync(GuildId, _permissions, _devices, _ledger, _handles, _bus,
            http ?? HttpWithDevice(), TestPrincipal.Create(userId));

    private Task<IResult> SetSharing(string userId, bool share) =>
        _endpoint.SetPhoneSharingAsync(GuildId, new SetPhoneSharingDto { Share = share },
            _permissions, _handles, _context, TestPrincipal.Create(userId));

    private static PaymentHandleDirectoryDto Body(IResult result) =>
        ((Ok<PaymentHandleDirectoryDto>)result).Value!;

    private GetUserPhoneNumbersRequest? PhoneRequest() =>
        _bus.Invoked.OfType<GetUserPhoneNumbersRequest>().SingleOrDefault();

    // ══════════════════════════════════════════════════════════════════════════ The opt-in starts
    // off ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ANewMemberIsNotSharingTheirNumber()
    {
        // The whole reason the flag is per-guild.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);

        Assert.Multiple(async () =>
        {
            Assert.That(await _handles.IsSharingPhoneAsync(GuildId, AnnaId), Is.False);
            Assert.That(await _handles.GetPhoneSharingMemberIdsAsync(GuildId, [AnnaId, BenId]), Is.Empty);
        });
    }

    [Test]
    public async Task ADefaultedMemberIsNotAskedAboutOnTheBusAtAll()
    {
        // Not "asked about and then filtered".
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);

        var body = Body(await Directory(AnnaId));

        Assert.Multiple(() =>
        {
            Assert.That(body.PhoneNumbers, Is.Empty);
            Assert.That(PhoneRequest(), Is.Null);
            Assert.That(body.SharingPhoneNumber, Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Turning it on and
    // off ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task TurningItOnMakesTheNumberVisibleToTheHouse()
    {
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswersWithNumbers((BenId, "+41792222222"));

        await SetSharing(BenId, share: true);
        var body = Body(await Directory(AnnaId));

        Assert.That(body.PhoneNumbers.Select(p => (p.UserId, p.PhoneNumber)),
            Is.EquivalentTo(new[] { (BenId, "+41792222222") }));
    }

    [Test]
    public async Task TurningItOffAgainWithdrawsTheNumber()
    {
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswersWithNumbers((BenId, "+41792222222"));

        await SetSharing(BenId, share: true);
        await SetSharing(BenId, share: false);
        var body = Body(await Directory(AnnaId));

        Assert.That(body.PhoneNumbers, Is.Empty);
    }

    [Test]
    public async Task TheCallersOwnStateIsEchoedBack()
    {
        // So the settings toggle renders without a second round trip.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswersWithNumbers((AnnaId, "+41791111111"));

        await SetSharing(AnnaId, share: true);
        var body = Body(await Directory(AnnaId));

        Assert.That(body.SharingPhoneNumber, Is.True);
    }

    [Test]
    public async Task SettingItWritesOnlyTheCallersOwnFlag()
    {
        // There is no parameter for whose flag is written, so this asserts the shape of the
        // endpoint: publishing somebody's phone number is not a moderator action and there is no
        // route that makes it one.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);

        await SetSharing(AnnaId, share: true);

        Assert.Multiple(async () =>
        {
            Assert.That(await _handles.IsSharingPhoneAsync(GuildId, AnnaId), Is.True);
            Assert.That(await _handles.IsSharingPhoneAsync(GuildId, BenId), Is.False);
        });
    }

    [Test]
    public async Task TheOptInIsScopedToOneGuild()
    {
        // The property the whole placement of this flag exists for.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = "guild-2", OwnerId = OwnerId, Name = "Some Community", Features = GuildFeatures.Ledger,
            Kind = GuildKind.Community,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-anna-2", GuildId = "guild-2", UserId = AnnaId,
            JoinedAt = DateTime.UtcNow, SearchValue = $"{AnnaId}#guild-2",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        await SetSharing(AnnaId, share: true);

        Assert.Multiple(async () =>
        {
            Assert.That(await _handles.IsSharingPhoneAsync(GuildId, AnnaId), Is.True);
            Assert.That(await _handles.IsSharingPhoneAsync("guild-2", AnnaId), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Only opted-in ids
    // cross the bus ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task IdentityIsAskedAboutTheOptedInMembersAndNobodyElse()
    {
        // The id list is the entire consent enforcement - GetUserPhoneNumbersRequest carries no
        // guild and Identity checks nothing on the far side.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId, CaraId);
        IdentityAnswersWithNumbers((BenId, "+41792222222"));

        await SetSharing(BenId, share: true);
        await Directory(AnnaId);

        Assert.That(PhoneRequest()!.UserIds, Is.EquivalentTo(new[] { BenId }));
    }

    [Test]
    public async Task AMemberWhoLeftIsNeitherAskedAboutNorReturned()
    {
        // Filtered against the live roster, so somebody who moved out stops being published even
        // while their member row is still there.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswersWithNumbers((BenId, "+41792222222"));
        await SetSharing(BenId, share: true);

        var ben = _context.GuildMembers.Single(m => m.UserId == BenId);
        _context.GuildMembers.Remove(ben);
        await _context.SaveChangesAsync();

        var body = Body(await Directory(AnnaId));

        Assert.Multiple(() =>
        {
            Assert.That(body.PhoneNumbers, Is.Empty);
            Assert.That(PhoneRequest(), Is.Null);
        });
    }

    [Test]
    public async Task ANumberIdentityVolunteersForSomebodyWhoDidNotOptInIsDropped()
    {
        // Identity is only ever asked about opted-in members, so this should be unreachable.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId, CaraId);
        IdentityAnswersWithNumbers((BenId, "+41792222222"), (CaraId, "+41793333333"));

        await SetSharing(BenId, share: true);
        var body = Body(await Directory(AnnaId));

        Assert.That(body.PhoneNumbers.Select(p => p.UserId), Is.EquivalentTo(new[] { BenId }));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Opted out is indistinguishable from having no number
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AnOptedOutMemberAndAMemberWithNoNumberProduceTheSameResponse()
    {
        // The property that has to hold, stated as directly as it can be: the two responses are
        // equal, field for field.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);

        // Ben has a number and has not opted in.
        IdentityAnswersWithNumbers((BenId, "+41792222222"));
        var optedOut = Body(await Directory(AnnaId));

        // Ben has opted in and has no number for Identity to give back.
        await SetSharing(BenId, share: true);
        IdentityAnswersWithNumbers();
        var noNumber = Body(await Directory(AnnaId));

        Assert.Multiple(() =>
        {
            Assert.That(optedOut.PhoneNumbers, Is.Empty);
            Assert.That(noNumber.PhoneNumbers, Is.Empty);
            Assert.That(noNumber.Members.Count, Is.EqualTo(optedOut.Members.Count));
            Assert.That(noNumber.SharingPhoneNumber, Is.EqualTo(optedOut.SharingPhoneNumber));
        });
    }

    [Test]
    public async Task TheResponseCarriesNoVerificationSignal()
    {
        // There is none to carry.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);
        IdentityAnswersWithNumbers((AnnaId, "+41791111111"));
        await SetSharing(AnnaId, share: true);

        var body = Body(await Directory(AnnaId));

        var properties = body.PhoneNumbers.Single().GetType().GetProperties().Select(p => p.Name);
        Assert.That(properties, Is.EquivalentTo(new[] { "UserId", "PhoneNumber", "UpdatedAt" }));
    }

    [Test]
    public async Task NumbersAndSealedHandlesStayInSeparateLists()
    {
        // Plaintext this server read on one side, ciphertext it cannot open on the other.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);
        IdentityAnswersWithNumbers((AnnaId, "+41791111111"));
        await SetSharing(AnnaId, share: true);

        await _handles.SealAsync(GuildId, AnnaId, new SealPaymentHandlesDto
        {
            Ciphertext = [0x01, 0x02], Nonce = [0x03], Version = 1,
        }, LedgerService.ComputeRosterVersion([AnnaId]));
        await _context.SaveChangesAsync();

        var body = Body(await Directory(AnnaId));

        Assert.Multiple(() =>
        {
            Assert.That(body.Members, Has.Count.EqualTo(1));
            Assert.That(body.PhoneNumbers, Has.Count.EqualTo(1));
            Assert.That(body.Members.Single().Ciphertext, Is.EqualTo(new byte[] { 0x01, 0x02 }));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ The gates
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ANonMemberCannotReadTheDirectory()
    {
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswersWithNumbers((BenId, "+41792222222"));
        await SetSharing(BenId, share: true);

        var result = await Directory(OutsiderId);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(PhoneRequest(), Is.Null, "the bus must not be reached before the gate passes");
        });
    }

    [Test]
    public async Task ANonMemberCannotOptIn()
    {
        // Membership is checked by the write finding a member row, so there is no roster read that
        // could disagree with it.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);

        var result = await SetSharing(OutsiderId, share: true);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task BothRoutesAreGatedOnTheLedgerFeature()
    {
        await SeedGuildAsync(GuildFeatures.None, AnnaId);

        Assert.Multiple(async () =>
        {
            Assert.That(await SetSharing(AnnaId, share: true), Is.InstanceOf<ForbidHttpResult>());
            Assert.That(await Directory(AnnaId), Is.InstanceOf<ForbidHttpResult>());
        });
    }

    [Test]
    public async Task Unauthenticated_ReturnsUnauthorized()
    {
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId);

        var result = await _endpoint.SetPhoneSharingAsync(GuildId, new SetPhoneSharingDto { Share = true },
            _permissions, _handles, _context, TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task AnUnidentifiedDeviceGetsNoNumbersEither()
    {
        // The directory already fails closed on an unresolvable device because the sealed wraps are
        // addressed to one.
        await SeedGuildAsync(GuildFeatures.Ledger, AnnaId, BenId);
        IdentityAnswersWithNumbers((BenId, "+41792222222"));
        await SetSharing(BenId, share: true);

        var result = await Directory(AnnaId, HttpWithDevice(deviceId: null));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(PhoneRequest(), Is.Null);
        });
    }
}
