using Messaging.Application.Dtos.Request;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Tests.Services;

/// <summary>
/// Admission to an encrypted context.
///
/// <para>The rules worth guarding are the ones that decide whether review means anything: an
/// approval is bound to exact key bytes, one person cannot count as two, and nobody vouches for
/// themselves.</para>
/// </summary>
[TestFixture]
public class MlsJoinRequestServiceTests
{
    private const string CHANNEL = "chan-1";
    private const string OWNER = "user-owner";
    private const string PEER = "user-peer";
    private const string REQUESTER = "user-new";

    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private TestMessagingContext _context = null!;
    private MlsJoinRequestService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _service = new MlsJoinRequestService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <param name="actors">Distinct users the server has seen act on the group. One means the
    /// threshold relaxes to a single approval, because nobody else exists to be the second.</param>
    private async Task SeedEncryptedChannel(params string[] actors)
    {
        var activatedBy = actors.Length > 0 ? actors[0] : OWNER;

        _context.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = CHANNEL,
            ChannelId = CHANNEL,
            Generation = 1,
            MlsGroupId = [1, 2, 3],
            Epoch = 0,
            ActivatedByUserId = activatedBy,
            ActivatedAt = T0,
        }));

        long epoch = 0;
        foreach (var actor in actors.Skip(1))
        {
            _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
            {
                ContextId = CHANNEL,
                ChannelId = CHANNEL,
                Generation = 1,
                Epoch = ++epoch,
                Commit = [1],
                SenderUserId = actor,
                SenderDeviceId = "device-" + actor,
            }));
        }

        await _context.SaveChangesAsync();
    }

    private static SubmitJoinRequestDto SubmitDto(byte tag = 1, string deviceId = "device-new") => new()
    {
        KeyPackage = [0x00, 0x01, 0x00, 0x01, tag],
        DeviceId = deviceId,
        SignatureKeyFingerprint = "AAAA-BBBB-CCCC",
    };

    private Task<MlsOperationResult> Submit(SubmitJoinRequestDto? dto = null, string requester = REQUESTER) =>
        _service.SubmitAsync(CHANNEL, null, CHANNEL, requester, dto ?? SubmitDto(), T0);

    // ══════════════════════════════════════════════════════════════════════════
    // Submitting
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Submit_OnAnEncryptedChannel_CreatesAPendingRequest()
    {
        await SeedEncryptedChannel(OWNER, PEER);

        var result = await Submit();

        var stored = await _context.MlsJoinRequests.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
            Assert.That(stored.State, Is.EqualTo(MlsJoinRequestState.Pending));
            Assert.That(stored.Generation, Is.EqualTo(1));
            Assert.That(stored.RequesterUserId, Is.EqualTo(REQUESTER));
        });
    }

    [Test]
    public async Task Submit_RecordsTheHashOfTheExactKeyPackage()
    {
        await SeedEncryptedChannel(OWNER, PEER);

        await Submit();

        // The hash is what the committing client checks before adding. Without it a malicious
        // server could swap the key between approval and add, and the review would guarantee
        // nothing about who actually got in.
        var stored = await _context.MlsJoinRequests.SingleAsync();
        Assert.That(stored.KeyPackageHash,
            Is.EqualTo(MlsJoinRequestService.HashKeyPackage(stored.KeyPackage)));
    }

    [Test]
    public async Task Submit_OnAPlainChannel_IsRejected()
    {
        var result = await Submit();

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.BadRequest));
    }

    [Test]
    public async Task Submit_TwiceWithTheSameKey_IsIdempotent()
    {
        await SeedEncryptedChannel(OWNER, PEER);

        await Submit();
        await Submit();

        // A client retrying on a flaky connection must not flood reviewers with the same ask.
        Assert.That(await _context.MlsJoinRequests.CountAsync(r => r.State == MlsJoinRequestState.Pending),
            Is.EqualTo(1));
    }

    [Test]
    public async Task Submit_WithANewKey_SupersedesTheOldRequest()
    {
        await SeedEncryptedChannel(OWNER, PEER);

        await Submit(SubmitDto(tag: 1));
        await Submit(SubmitDto(tag: 2));

        // Reviewers should never face two live asks from one device and have to work out which one
        // they are vouching for.
        var pending = await _context.MlsJoinRequests.Where(r => r.State == MlsJoinRequestState.Pending).ToListAsync();
        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending[0].KeyPackage, Is.EqualTo(new byte[] { 0x00, 0x01, 0x00, 0x01, 2 }));
    }

    [Test]
    public async Task Submit_FromASecondDevice_IsItsOwnRequest()
    {
        await SeedEncryptedChannel(OWNER, PEER);

        await Submit(SubmitDto(deviceId: "device-phone"));
        await Submit(SubmitDto(deviceId: "device-laptop"));

        // Each device holds its own leaf, so admission is per device.
        Assert.That(await _context.MlsJoinRequests.CountAsync(), Is.EqualTo(2));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The threshold
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Threshold_IsTwoWhenTheGroupHasSeenTwoPeople()
    {
        await SeedEncryptedChannel(OWNER, PEER);

        Assert.That(await _service.RequiredApprovalsFor(CHANNEL, 1, MlsContextKind.Channel), Is.EqualTo(2));
    }

    [Test]
    public async Task Threshold_RelaxesToOneWhenOnlyOnePersonHasEverActed()
    {
        await SeedEncryptedChannel(OWNER);

        // Demanding two here would leave the group permanently unable to admit anyone: the single
        // member cannot approve twice, and nobody else exists to be the second.
        Assert.That(await _service.RequiredApprovalsFor(CHANNEL, 1, MlsContextKind.Channel), Is.EqualTo(1));
    }

    [Test]
    public async Task Threshold_CountsDistinctPeopleNotCommits()
    {
        await SeedEncryptedChannel(OWNER, OWNER, OWNER);

        // One person committing repeatedly is still one person.
        Assert.That(await _service.RequiredApprovalsFor(CHANNEL, 1, MlsContextKind.Channel), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Approving
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<string> PendingRequestId()
    {
        await Submit();
        return (await _context.MlsJoinRequests.SingleAsync()).Id;
    }

    [Test]
    public async Task Approve_BelowTheThreshold_WithholdsTheKeyPackage()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        var result = await _service.ApproveAsync(CHANNEL, id, OWNER, T0);

        var payload = (MlsJoinRequestApprovalResultDto)result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(payload.Approvals, Is.EqualTo(1));
            Assert.That(payload.ThresholdMet, Is.False);
            // A lone approver has no business holding bytes they are not yet entitled to add.
            Assert.That(payload.KeyPackage, Is.Null);
        });
    }

    [Test]
    public async Task Approve_MeetingTheThreshold_HandsOverTheKeyPackage()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        await _service.ApproveAsync(CHANNEL, id, OWNER, T0);
        var result = await _service.ApproveAsync(CHANNEL, id, PEER, T0);

        var payload = (MlsJoinRequestApprovalResultDto)result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(payload.ThresholdMet, Is.True);
            Assert.That(payload.KeyPackage, Is.Not.Null);
            Assert.That(payload.KeyPackageHash,
                Is.EqualTo(MlsJoinRequestService.HashKeyPackage(payload.KeyPackage!)));
        });
    }

    [Test]
    public async Task Approve_TwiceByTheSamePerson_CountsOnce()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        await _service.ApproveAsync(CHANNEL, id, OWNER, T0);
        var result = await _service.ApproveAsync(CHANNEL, id, OWNER, T0);

        // Approving from a phone and then a laptop is one person twice - which is exactly what the
        // two-person rule exists to prevent.
        var payload = (MlsJoinRequestApprovalResultDto)result.Value!;
        Assert.That(payload.Approvals, Is.EqualTo(1));
        Assert.That(payload.ThresholdMet, Is.False);
    }

    [Test]
    public async Task Approve_ByTheRequester_IsRejected()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        var result = await _service.ApproveAsync(CHANNEL, id, REQUESTER, T0);

        // Vouching for yourself is not review.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.BadRequest));
        Assert.That(await _context.MlsJoinRequestApprovals.AnyAsync(), Is.False);
    }

    [Test]
    public async Task Approve_OnASingleActorGroup_MeetsTheThresholdAlone()
    {
        await SeedEncryptedChannel(OWNER);
        var id = await PendingRequestId();

        var result = await _service.ApproveAsync(CHANNEL, id, OWNER, T0);

        var payload = (MlsJoinRequestApprovalResultDto)result.Value!;
        Assert.That(payload.ThresholdMet, Is.True);
    }

    [Test]
    public async Task Approve_AnExpiredRequest_IsRefused()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        var result = await _service.ApproveAsync(
            CHANNEL, id, OWNER, T0 + MlsJoinRequest.Lifetime + TimeSpan.FromDays(1));

        // A months-old request may be against a key the requester has long since rotated away from.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
    }

    [Test]
    public async Task Approve_ADeniedRequest_IsRefused()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();
        await _service.DenyAsync(CHANNEL, id, OWNER, T0);

        var result = await _service.ApproveAsync(CHANNEL, id, PEER, T0);

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Denying and cancelling
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Deny_NeedsOnlyOnePerson()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        await _service.DenyAsync(CHANNEL, id, OWNER, T0);

        // Refusing to vouch does not need a second opinion; the requester can ask again.
        var stored = await _context.MlsJoinRequests.SingleAsync();
        Assert.That(stored.State, Is.EqualTo(MlsJoinRequestState.Denied));
        Assert.That(stored.DeniedByUserId, Is.EqualTo(OWNER));
    }

    [Test]
    public async Task Cancel_ByAnotherUser_ReportsNotFound()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        var result = await _service.CancelAsync(CHANNEL, id, PEER, T0);

        // Not-found rather than forbidden, so an id cannot be probed for existence.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.NotFound));
        Assert.That((await _context.MlsJoinRequests.SingleAsync()).State,
            Is.EqualTo(MlsJoinRequestState.Pending));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Listing and fulfilment
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task List_ExcludesExpiredAndResolvedRequests()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        await Submit(SubmitDto(deviceId: "device-a"));
        await Submit(SubmitDto(deviceId: "device-b"));
        var denied = await _context.MlsJoinRequests.FirstAsync(r => r.RequesterDeviceId == "device-a");
        await _service.DenyAsync(CHANNEL, denied.Id, OWNER, T0);

        var pending = await _service.ListPendingAsync(CHANNEL, T0);

        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending[0].RequesterDeviceId, Is.EqualTo("device-b"));
    }

    [Test]
    public async Task List_ReportsTheThresholdAndWhoHasVouched()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();
        await _service.ApproveAsync(CHANNEL, id, OWNER, T0);

        var pending = await _service.ListPendingAsync(CHANNEL, T0);

        Assert.Multiple(() =>
        {
            Assert.That(pending[0].RequiredApprovals, Is.EqualTo(2));
            // A second opinion is only worth something if you can see whose the first was.
            Assert.That(pending[0].ApproverUserIds, Is.EquivalentTo(new[] { OWNER }));
        });
    }

    [Test]
    public async Task Fulfil_ClosesOnlyTheNamedRequests()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        await Submit(SubmitDto(deviceId: "device-a"));
        await Submit(SubmitDto(deviceId: "device-b"));
        var admitted = await _context.MlsJoinRequests.FirstAsync(r => r.RequesterDeviceId == "device-a");

        await _service.FulfilAsync(CHANNEL, [admitted.Id], T0);
        await _context.SaveChangesAsync();

        var states = await _context.MlsJoinRequests
            .ToDictionaryAsync(r => r.RequesterDeviceId, r => r.State);
        Assert.Multiple(() =>
        {
            Assert.That(states["device-a"], Is.EqualTo(MlsJoinRequestState.Fulfilled));
            Assert.That(states["device-b"], Is.EqualTo(MlsJoinRequestState.Pending));
        });
    }

    [Test]
    public async Task Fulfil_IsScopedToTheContext()
    {
        await SeedEncryptedChannel(OWNER, PEER);
        var id = await PendingRequestId();

        await _service.FulfilAsync("some-other-channel", [id], T0);
        await _context.SaveChangesAsync();

        Assert.That((await _context.MlsJoinRequests.SingleAsync()).State,
            Is.EqualTo(MlsJoinRequestState.Pending));
    }
}
