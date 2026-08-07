using Guild.Application.Dtos.Request;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>Storage behaviour for the sealed payment handles.</summary>
[TestFixture]
public class PaymentHandleServiceTests
{
    private const string GuildId = "gild-1";

    private SealedHandleContext _context = null!;
    private PaymentHandleService _handles = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new SealedHandleContext(Guid.NewGuid().ToString());
        _handles = new PaymentHandleService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>The InMemory context plus the two entity configurations that are still waiting on
    /// the integration pass into <c>MicroserviceContext</c>. Written out here rather than stubbed
    /// so these tests exercise the mapping that will actually ship, including the composite key
    /// that makes "one wrap per device" a database rule and not just an endpoint check.</summary>
    private sealed class SealedHandleContext(string dbName) : MicroserviceContext(
        new DbContextOptionsBuilder<MicroserviceContext>().UseInMemoryDatabase(dbName).Options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Left empty for the same reason TestGuildContext does: calling base would add a
            // conflicting Postgres provider.
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<PaymentHandleBlob>(blobBuilder =>
            {
                blobBuilder.HasOne<Guild.Domain.Aggregates.Guild>()
                    .WithMany()
                    .HasForeignKey(x => x.GuildId)
                    .OnDelete(DeleteBehavior.Cascade);

                blobBuilder.HasIndex(x => new { x.GuildId, x.UserId }).IsUnique();
            });

            modelBuilder.Entity<PaymentHandleKeyWrap>(wrapBuilder =>
            {
                wrapBuilder.HasKey(x => new { x.PaymentHandleBlobId, x.RecipientDeviceId });

                wrapBuilder.HasOne(x => x.Blob)
                    .WithMany(x => x.Wraps)
                    .HasForeignKey(x => x.PaymentHandleBlobId)
                    .OnDelete(DeleteBehavior.Cascade);

                wrapBuilder.HasIndex(x => x.RecipientDeviceId);
            });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SealPaymentHandlesDto Seal(byte marker, params (string User, string Device)[] wraps) => new()
    {
        Ciphertext = [marker, marker, marker],
        Nonce = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
        Version = 1,
        Wraps = wraps
            .Select(w => new PaymentHandleWrapDto
            {
                RecipientUserId = w.User,
                RecipientDeviceId = w.Device,
                WrappedKey = [marker, 0xAA],
            })
            .ToList(),
    };

    private static HashSet<string> Members(params string[] userIds) => new(userIds, StringComparer.Ordinal);

    private async Task<PaymentHandleBlob> SealAsync(string userId, SealPaymentHandlesDto dto, int rosterVersion = 1)
    {
        var blob = await _handles.SealAsync(GuildId, userId, dto, rosterVersion);
        await _context.SaveChangesAsync();
        return blob;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Validation - sizes and identities, and nothing about the contents
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Validate_AcceptsAWellFormedSeal()
    {
        var error = PaymentHandleService.Validate(
            Seal(1, ("anna", "dev-anna-phone"), ("ben", "dev-ben-laptop")), Members("anna", "ben"));

        Assert.That(error, Is.Null);
    }

    /// <summary>The edge case, and a legal one: a client that has not fetched the recipient roster
    /// yet must still be able to store its own details. Sealed to nobody is a state, not an error -
    /// the owner's own devices can already open it.</summary>
    [Test]
    public void Validate_AcceptsASealWithNoWraps()
    {
        Assert.That(PaymentHandleService.Validate(Seal(1), Members("anna")), Is.Null);
    }

    /// <summary>The rule the server is still the only one able to enforce.</summary>
    [Test]
    public void Validate_RejectsAWrapForSomebodyWhoIsNotAMember()
    {
        var error = PaymentHandleService.Validate(
            Seal(1, ("anna", "dev-1"), ("stranger", "dev-2")), Members("anna", "ben"));

        Assert.That(error, Does.Contain("member of this guild"));
    }

    /// <summary>The device is half the row's primary key, so a repeat would otherwise surface as a
    /// constraint violation and a 500 instead of the client bug it is.</summary>
    [Test]
    public void Validate_RejectsTheSameDeviceTwice()
    {
        var error = PaymentHandleService.Validate(
            Seal(1, ("anna", "dev-1"), ("anna", "dev-1")), Members("anna"));

        Assert.That(error, Does.Contain("only be wrapped once"));
    }

    [Test]
    public void Validate_RejectsAnEmptyOrOversizePayload()
    {
        var empty = Seal(1);
        empty.Ciphertext = [];

        var huge = Seal(1);
        huge.Ciphertext = new byte[PaymentHandleService.MaxCiphertextBytes + 1];

        var noNonce = Seal(1);
        noNonce.Nonce = [];

        Assert.Multiple(() =>
        {
            Assert.That(PaymentHandleService.Validate(empty, Members("anna")), Does.Contain("Ciphertext"));
            Assert.That(PaymentHandleService.Validate(huge, Members("anna")), Does.Contain("bytes or fewer"));
            Assert.That(PaymentHandleService.Validate(noNonce, Members("anna")), Does.Contain("Nonce"));
        });
    }

    [Test]
    public void Validate_RejectsTooManyWraps()
    {
        var dto = Seal(1);
        dto.Wraps = Enumerable.Range(0, PaymentHandleService.MaxWraps + 1)
            .Select(i => new PaymentHandleWrapDto
            {
                RecipientUserId = "anna", RecipientDeviceId = $"dev-{i}", WrappedKey = [1],
            })
            .ToList();

        Assert.That(PaymentHandleService.Validate(dto, Members("anna")), Does.Contain("wraps may be sent"));
    }

    /// <summary>Ciphertext that decrypts to nothing at all is accepted, and that is the point. The
    /// server has no opinion about the payload because it cannot form one; if this ever starts
    /// failing, something has learned to read the blob.</summary>
    [Test]
    public void Validate_HasNoOpinionAboutWhatThePayloadContains()
    {
        var garbage = Seal(1);
        garbage.Ciphertext = [0xFF, 0x00, 0xFF];

        Assert.That(PaymentHandleService.Validate(garbage, Members("anna")), Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════ Wholesale
    // replacement ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Seal_StoresOneBlobPerMemberPerGuild()
    {
        await SealAsync("anna", Seal(1, ("anna", "dev-anna")));
        await SealAsync("ben", Seal(2, ("ben", "dev-ben")));

        var blobs = await _context.Set<PaymentHandleBlob>().ToListAsync();

        Assert.That(blobs.Select(b => b.UserId), Is.EquivalentTo(new[] { "anna", "ben" }));
    }

    /// <summary>Re-sealing replaces; it does not accumulate.</summary>
    [Test]
    public async Task Seal_Twice_ReplacesTheBlobRatherThanAddingASecond()
    {
        var first = await SealAsync("anna", Seal(1, ("anna", "dev-anna")));
        var second = await SealAsync("anna", Seal(9, ("anna", "dev-anna")), rosterVersion: 4);

        var blobs = await _context.Set<PaymentHandleBlob>().AsNoTracking().ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(blobs, Has.Count.EqualTo(1));
            Assert.That(second.Id, Is.EqualTo(first.Id), "the row is updated, not swapped");
            Assert.That(blobs[0].Ciphertext, Is.EqualTo(new byte[] { 9, 9, 9 }));
            Assert.That(blobs[0].MemberRosterVersion, Is.EqualTo(4));
        });
    }

    /// <summary>The half of "wholesale" that is easy to get wrong: the wraps go too.</summary>
    [Test]
    public async Task Seal_Twice_DropsWrapsThatWereNotResent()
    {
        await SealAsync("anna", Seal(1, ("anna", "dev-anna"), ("ben", "dev-ben")));
        await SealAsync("anna", Seal(2, ("anna", "dev-anna")));

        var wraps = await _context.Set<PaymentHandleKeyWrap>().AsNoTracking().ToListAsync();

        Assert.That(wraps.Select(w => w.RecipientDeviceId), Is.EquivalentTo(new[] { "dev-anna" }));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Reading - one device's key, and only that one
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReadForDevice_ReturnsOnlyTheAskingDevicesWrap()
    {
        await SealAsync("anna", Seal(1, ("anna", "dev-anna"), ("ben", "dev-ben-phone"), ("ben", "dev-ben-laptop")));

        var forBensPhone = await _handles.ReadForDeviceAsync(GuildId, "dev-ben-phone", ["anna", "ben"]);

        Assert.Multiple(() =>
        {
            Assert.That(forBensPhone, Has.Count.EqualTo(1));
            Assert.That(forBensPhone[0].UserId, Is.EqualTo("anna"));
            Assert.That(forBensPhone[0].WrappedKey, Is.Not.Null,
                "Ben's phone was sealed to, so it gets a key");
        });

        // The structural guarantee: nothing in the response describes Ben's other device or Anna's.
        var stored = await _context.Set<PaymentHandleKeyWrap>().AsNoTracking().CountAsync();
        Assert.That(stored, Is.EqualTo(3), "three wraps exist, and exactly one of them was returned");
    }

    /// <summary>The edge case somebody who joined yesterday hits: the blob is visible, the key is
    /// not, and that is a normal state to render rather than an error. A client showing "Anna has
    /// not shared how to pay her with you yet" is correct here.</summary>
    [Test]
    public async Task ReadForDevice_ReturnsTheBlobWithNoKeyForADeviceNobodySealedTo()
    {
        await SealAsync("anna", Seal(1, ("anna", "dev-anna")));

        var forNewcomer = await _handles.ReadForDeviceAsync(GuildId, "dev-cara", ["anna", "cara"]);

        Assert.Multiple(() =>
        {
            Assert.That(forNewcomer, Has.Count.EqualTo(1));
            Assert.That(forNewcomer[0].WrappedKey, Is.Null);
            Assert.That(forNewcomer[0].Ciphertext, Is.Not.Empty, "the ciphertext is not a secret from members");
        });
    }

    /// <summary>The negative case: somebody who has moved out drops off the directory even while
    /// their row is still in the table, so a departed flatmate does not keep appearing on the
    /// settle screen.</summary>
    [Test]
    public async Task ReadForDevice_OmitsPeopleWhoAreNoLongerMembers()
    {
        await SealAsync("anna", Seal(1, ("anna", "dev-anna")));
        await SealAsync("ben", Seal(2, ("ben", "dev-ben")));

        var afterBenMovesOut = await _handles.ReadForDeviceAsync(GuildId, "dev-anna", ["anna"]);

        Assert.That(afterBenMovesOut.Select(b => b.UserId), Is.EquivalentTo(new[] { "anna" }));
    }

    [Test]
    public async Task ReadForDevice_OnAnEmptyGuildReturnsNothing()
    {
        Assert.That(await _handles.ReadForDeviceAsync(GuildId, "dev-anna", ["anna"]), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Deletion
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Delete_RemovesTheBlobAndEveryWrapOfIt()
    {
        await SealAsync("anna", Seal(1, ("anna", "dev-anna"), ("ben", "dev-ben")));

        var removed = await _handles.DeleteAsync(GuildId, "anna");
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(removed, Is.True);
            Assert.That(await _context.Set<PaymentHandleBlob>().CountAsync(), Is.EqualTo(0));
            Assert.That(await _context.Set<PaymentHandleKeyWrap>().CountAsync(), Is.EqualTo(0));
        });
    }

    /// <summary>Deleting details you never recorded is not an error.</summary>
    [Test]
    public async Task Delete_WithNothingStoredIsNotAnError()
    {
        Assert.That(await _handles.DeleteAsync(GuildId, "anna"), Is.False);
    }

    /// <summary>Deletion is scoped to the owner, which is what makes "your own only" true at the
    /// storage layer as well as at the route.</summary>
    [Test]
    public async Task Delete_LeavesEverybodyElsesBlobAlone()
    {
        await SealAsync("anna", Seal(1, ("anna", "dev-anna")));
        await SealAsync("ben", Seal(2, ("ben", "dev-ben")));

        await _handles.DeleteAsync(GuildId, "anna");
        await _context.SaveChangesAsync();

        var remaining = await _context.Set<PaymentHandleBlob>().AsNoTracking().ToListAsync();

        Assert.That(remaining.Select(b => b.UserId), Is.EquivalentTo(new[] { "ben" }));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Roster version - the staleness signal
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void RosterVersion_DoesNotDependOnTheOrderMembersAreRead()
    {
        Assert.That(LedgerService.ComputeRosterVersion(["anna", "ben", "cara"]),
            Is.EqualTo(LedgerService.ComputeRosterVersion(["cara", "anna", "ben"])));
    }

    [Test]
    public void RosterVersion_ChangesWhenSomebodyJoinsOrLeaves()
    {
        var before = LedgerService.ComputeRosterVersion(["anna", "ben"]);

        Assert.Multiple(() =>
        {
            Assert.That(LedgerService.ComputeRosterVersion(["anna", "ben", "cara"]), Is.Not.EqualTo(before));
            Assert.That(LedgerService.ComputeRosterVersion(["anna"]), Is.Not.EqualTo(before));
        });
    }

    /// <summary>The edge case that a naive "member count" version would get wrong: one flatmate
    /// replaced by another is a roster change, and every wrap sealed before it is stale.</summary>
    [Test]
    public void RosterVersion_ChangesWhenOneMemberReplacesAnother()
    {
        Assert.That(LedgerService.ComputeRosterVersion(["anna", "ben"]),
            Is.Not.EqualTo(LedgerService.ComputeRosterVersion(["anna", "cara"])));
    }

    /// <summary>Non-negative, because the value is shown to whoever is debugging a client that will
    /// not re-seal, and a negative number reads like a sentinel.</summary>
    [Test]
    public void RosterVersion_IsNonNegativeAndStable()
    {
        var once = LedgerService.ComputeRosterVersion(["anna", "ben", "cara", "dan"]);

        Assert.Multiple(() =>
        {
            Assert.That(once, Is.GreaterThanOrEqualTo(0));
            Assert.That(LedgerService.ComputeRosterVersion(["anna", "ben", "cara", "dan"]), Is.EqualTo(once));
            Assert.That(LedgerService.ComputeRosterVersion([]), Is.GreaterThanOrEqualTo(0));
        });
    }
}
