using Echo.Realtime.Devices;
using Guild.Application.Bus.Events.Privacy;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Events;

/// <summary>Removing the number revokes the consent to publish it.</summary>
[TestFixture]
public class UserPhoneNumberRemovedHandlerTests
{
    private const string FlatId = "guild-flat";
    private const string HouseId = "guild-house";
    private const string OwnerId = "owner-1";
    private const string AnnaId = "user-anna";
    private const string BenId = "user-ben";
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

        _bus.SetResponse<ValidateUserDeviceRequest>(new ValidateUserDeviceResponse { IsRegistered = true });
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task SeedGuildAsync(string guildId, params string[] memberUserIds)
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = guildId, OwnerId = OwnerId, Name = guildId, Features = GuildFeatures.Ledger,
            Kind = GuildKind.Household,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var userId in memberUserIds)
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"member-{guildId}-{userId}", GuildId = guildId, UserId = userId,
                JoinedAt = DateTime.UtcNow, SearchValue = $"{userId}#{guildId}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    private async Task ShareAsync(string guildId, string userId)
    {
        await _handles.SetPhoneSharingAsync(guildId, userId, share: true);
        await _context.SaveChangesAsync();
    }

    /// <summary>The handler stages only - Wolverine's DbContext middleware commits in production -
    /// so the tests have to supply that one commit, since EF Core's InMemory provider does not
    /// reflect uncommitted changes in a no-tracking query.</summary>
    private async Task NumberRemovedAsync(string userId)
    {
        await UserPhoneNumberRemovedHandler.Handle(
            new UserPhoneNumberRemovedEvent { UserId = userId },
            _context,
            NullLogger<UserPhoneNumberRemovedHandler>.Instance);

        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════ Normal
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RemovingTheNumberClearsTheOptInInEveryGuildAtOnce()
    {
        // The consent is per guild; the number is not.
        await SeedGuildAsync(FlatId, AnnaId, BenId);
        await SeedGuildAsync(HouseId, AnnaId);
        await ShareAsync(FlatId, AnnaId);
        await ShareAsync(HouseId, AnnaId);

        await NumberRemovedAsync(AnnaId);

        Assert.Multiple(async () =>
        {
            Assert.That(await _handles.IsSharingPhoneAsync(FlatId, AnnaId), Is.False);
            Assert.That(await _handles.IsSharingPhoneAsync(HouseId, AnnaId), Is.False);
        });
    }

    [Test]
    public async Task AfterClearingTheAccountIsNotNamedOnTheBusAgain()
    {
        // The point of the whole fix, stated on the read path: adding a new number later must not
        // put it in front of the house.
        await SeedGuildAsync(FlatId, AnnaId, BenId);
        await ShareAsync(FlatId, BenId);

        await NumberRemovedAsync(BenId);

        // Identity would answer for Ben's replacement number if it were ever asked.
        _bus.SetResponse<GetUserPhoneNumbersRequest>(new GetUserPhoneNumbersResponse
        {
            PhoneNumbers =
            [
                new UserPhoneNumberDto
                {
                    UserId = BenId, PhoneNumber = "+41799999999", UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });

        var http = new DefaultHttpContext();
        http.Request.Headers[DeviceIdentity.HeaderName] = AnnaDeviceId;

        var result = await _endpoint.GetAsync(FlatId, _permissions, _devices, _ledger, _handles,
            _bus, http, TestPrincipal.Create(AnnaId));

        var body = ((Ok<PaymentHandleDirectoryDto>)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(body.PhoneNumbers, Is.Empty);
            Assert.That(_bus.Invoked.OfType<GetUserPhoneNumbersRequest>(), Is.Empty,
                "a cleared opt-in must leave the account unmentioned on the bus, not filtered "
                + "out of an answer about it");
        });
    }

    [Test]
    public async Task TheOwnerCanShareAgainAfterwards()
    {
        // Revocation, not a lockout.
        await SeedGuildAsync(FlatId, AnnaId);
        await ShareAsync(FlatId, AnnaId);
        await NumberRemovedAsync(AnnaId);

        await ShareAsync(FlatId, AnnaId);

        Assert.That(await _handles.IsSharingPhoneAsync(FlatId, AnnaId), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════ Edge - replay and
    // nothing to do ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AnAccountWithNoOptInsAnywhereIsANoOp()
    {
        // The common case, and it must not be an error: the durable outbox retries anything that
        // throws, so a handler that objected to having nothing to do would redeliver forever and
        // then park in an error queue.
        await SeedGuildAsync(FlatId, AnnaId, BenId);

        Assert.DoesNotThrowAsync(() => NumberRemovedAsync(AnnaId));
        Assert.That(await _handles.IsSharingPhoneAsync(FlatId, AnnaId), Is.False);
    }

    [Test]
    public async Task AnAccountWithNoMemberRowsAtAllIsANoOp()
    {
        // Nobody's guild, nobody's member row.
        Assert.DoesNotThrowAsync(() => NumberRemovedAsync("user-stranger"));
    }

    [Test]
    public async Task RedeliveringTheSameEventChangesNothingFurther()
    {
        // Idempotent by construction - the query only loads rows that are set - so the second and
        // third deliveries find nothing and leave the re-share below standing.
        await SeedGuildAsync(FlatId, AnnaId);
        await ShareAsync(FlatId, AnnaId);

        await NumberRemovedAsync(AnnaId);
        await ShareAsync(FlatId, AnnaId);

        // A duplicate of the original delivery, arriving after the user deliberately re-shared.
        await NumberRemovedAsync(AnnaId);
        await NumberRemovedAsync(AnnaId);

        Assert.That(await _handles.IsSharingPhoneAsync(FlatId, AnnaId), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════ Negative
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NobodyElsesOptInIsTouched()
    {
        // The event names one account.
        await SeedGuildAsync(FlatId, AnnaId, BenId);
        await SeedGuildAsync(HouseId, BenId);
        await ShareAsync(FlatId, AnnaId);
        await ShareAsync(FlatId, BenId);
        await ShareAsync(HouseId, BenId);

        await NumberRemovedAsync(AnnaId);

        Assert.Multiple(async () =>
        {
            Assert.That(await _handles.IsSharingPhoneAsync(FlatId, AnnaId), Is.False);
            Assert.That(await _handles.IsSharingPhoneAsync(FlatId, BenId), Is.True);
            Assert.That(await _handles.IsSharingPhoneAsync(HouseId, BenId), Is.True);
        });
    }

    [Test]
    public async Task NothingElseOnTheMemberRowIsDisturbed()
    {
        // The handler writes one flag.
        await SeedGuildAsync(FlatId, AnnaId);
        await ShareAsync(FlatId, AnnaId);

        var before = _context.GuildMembers.Single(m => m.UserId == AnnaId && m.GuildId == FlatId);
        var joinedAt = before.JoinedAt;
        var searchValue = before.SearchValue;

        await NumberRemovedAsync(AnnaId);

        var after = _context.GuildMembers.Single(m => m.UserId == AnnaId && m.GuildId == FlatId);
        Assert.Multiple(() =>
        {
            Assert.That(after.JoinedAt, Is.EqualTo(joinedAt));
            Assert.That(after.SearchValue, Is.EqualTo(searchValue));
            Assert.That(after.SharePhoneForPayments, Is.False);
        });
    }

    [Test]
    public void TheEventCarriesTheUserIdAndNothingElse()
    {
        // Asserted rather than left to review.
        var properties = typeof(UserPhoneNumberRemovedEvent).GetProperties().Select(p => p.Name);

        Assert.That(properties, Is.EquivalentTo(new[] { nameof(UserPhoneNumberRemovedEvent.UserId) }));
    }
}
