using Domain;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Tests.Services;

/// <summary>
/// Admission of a new device to an existing encrypted context: the approval threshold, and the
/// proof relay that lets it happen without a human when the account allows it.
/// </summary>
[TestFixture]
public class MlsAdmissionTests
{
    private const string ConversationId = "conv-1";
    private const string ChannelId = "chan-1";
    private const string OwnerId = "user-owner";
    private const string RequesterId = "user-owner-second-device-owner";

    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

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

    private async Task SeedEncryptedContext(string contextId, string? conversationId, string activatedBy)
    {
        _context.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = contextId,
            ConversationId = conversationId,
            ChannelId = conversationId is null ? contextId : null,
            Generation = 1,
            MlsGroupId = [1, 2, 3],
            Epoch = 0,
            ActivatedByUserId = activatedBy,
            ActivatedAt = T0,
        }));
        await _context.SaveChangesAsync();
    }

    private static SubmitJoinRequestDto SubmitDto(string deviceId = "device-new") => new()
    {
        KeyPackage = [0x00, 0x01, 0x00, 0x01, 0x42],
        DeviceId = deviceId,
        SignatureKeyFingerprint = "AAAA-BBBB-CCCC",
        DeviceName = "New phone",
    };

    // ══════════════════════════════════════════════════════════════════════════ The threshold
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Threshold_ForAConversation_IsAlwaysOne()
    {
        await SeedEncryptedContext(ConversationId, ConversationId, OwnerId);
        // Two people have acted, which for a channel would mean two approvals.
        _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = ConversationId, ConversationId = ConversationId, Generation = 1, Epoch = 1,
            Commit = [1], SenderUserId = "someone-else", SenderDeviceId = "d",
        }));
        await _context.SaveChangesAsync();

        var required = await _service.RequiredApprovalsFor(ConversationId, 1, MlsContextKind.Conversation);

        // A DM has two humans in it.
        Assert.That(required, Is.EqualTo(1));
    }

    [Test]
    public async Task Threshold_TreatsAnEmptyActivatedByAsNoActor()
    {
        // The AddMlsGenerations backfill wrote activated_by_user_id = '' for every pre-existing
        // generation.
        await SeedEncryptedContext(ChannelId, null, activatedBy: "");
        _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = ChannelId, ChannelId = ChannelId, Generation = 1, Epoch = 1,
            Commit = [1], SenderUserId = OwnerId, SenderDeviceId = "d",
        }));
        await _context.SaveChangesAsync();

        var required = await _service.RequiredApprovalsFor(ChannelId, 1, MlsContextKind.Channel);

        Assert.That(required, Is.EqualTo(1));
    }

    [Test]
    public async Task Threshold_ForAChannelWithTwoRealActors_IsStillTwo()
    {
        await SeedEncryptedContext(ChannelId, null, OwnerId);
        _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = ChannelId, ChannelId = ChannelId, Generation = 1, Epoch = 1,
            Commit = [1], SenderUserId = "second-person", SenderDeviceId = "d",
        }));
        await _context.SaveChangesAsync();

        Assert.That(await _service.RequiredApprovalsFor(ChannelId, 1, MlsContextKind.Channel), Is.EqualTo(2));
    }

    // ══════════════════════════════════════════════════════════════════════════ Auto-admission
    // budget ══════════════════════════════════════════════════════════════════════════

    private Task<MlsOperationResult> Submit(
        ProtectionLevel level, string deviceId = "device-new", DateTimeOffset? at = null) =>
        _service.SubmitAsync(ConversationId, ConversationId, null, RequesterId, SubmitDto(deviceId),
            at ?? T0, MlsContextKind.Conversation, level);

    [Test]
    public async Task Submit_UnderVerifiedDevices_AlwaysRequiresAHuman()
    {
        await SeedEncryptedContext(ConversationId, ConversationId, OwnerId);

        var result = await Submit(ProtectionLevel.VerifiedDevices);

        var request = (MlsJoinRequestDto)result.Value!;
        Assert.That(request.RequiresManualApproval, Is.True);
    }

    [Test]
    public async Task Submit_UnderTrustedSignIn_MayBeSatisfiedByProofAlone()
    {
        await SeedEncryptedContext(ConversationId, ConversationId, OwnerId);

        var result = await Submit(ProtectionLevel.TrustedSignIn);

        var request = (MlsJoinRequestDto)result.Value!;
        Assert.That(request.RequiresManualApproval, Is.False);
    }

    [Test]
    public async Task Submit_AfterADeviceWasAlreadyAutoAdmittedToday_FallsBackToManual()
    {
        await SeedEncryptedContext(ConversationId, ConversationId, OwnerId);

        // An earlier device of the same account went in automatically an hour ago.
        var earlier = MlsJoinRequest.Create(new CreateMlsJoinRequestParams
        {
            ContextId = "conv-other", ConversationId = "conv-other", Generation = 1,
            RequesterUserId = RequesterId, RequesterDeviceId = "device-earlier",
            KeyPackage = [1], KeyPackageHash = "h", SignatureKeyFingerprint = "f",
            CreatedAt = T0, ExpiresAt = T0 + MlsJoinRequest.Lifetime, RequiresManualApproval = false,
        });
        earlier.State = MlsJoinRequestState.Fulfilled;
        earlier.FulfilledAt = T0 + TimeSpan.FromHours(1);
        _context.MlsJoinRequests.Add(earlier);
        await _context.SaveChangesAsync();

        var result = await Submit(ProtectionLevel.TrustedSignIn, "device-second",
            at: T0 + TimeSpan.FromHours(2));

        // A burst of admissions is what a compromise looks like, so the second one costs a tap.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
        Assert.That(((MlsJoinRequestDto)result.Value!).RequiresManualApproval, Is.True);
    }

    [Test]
    public async Task Submit_ForTheSameDeviceInASecondConversation_KeepsItsBudget()
    {
        await SeedEncryptedContext(ConversationId, ConversationId, OwnerId);

        var earlier = MlsJoinRequest.Create(new CreateMlsJoinRequestParams
        {
            ContextId = "conv-other", ConversationId = "conv-other", Generation = 1,
            RequesterUserId = RequesterId, RequesterDeviceId = "device-new",
            KeyPackage = [1], KeyPackageHash = "h", SignatureKeyFingerprint = "f",
            CreatedAt = T0, ExpiresAt = T0 + MlsJoinRequest.Lifetime, RequiresManualApproval = false,
        });
        earlier.State = MlsJoinRequestState.Fulfilled;
        earlier.FulfilledAt = T0 + TimeSpan.FromHours(1);
        _context.MlsJoinRequests.Add(earlier);
        await _context.SaveChangesAsync();

        var result = await Submit(ProtectionLevel.TrustedSignIn, "device-new",
            at: T0 + TimeSpan.FromHours(2));

        // The budget counts devices, not requests.
        Assert.That(((MlsJoinRequestDto)result.Value!).RequiresManualApproval, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════ The proof relay
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly byte[] Nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private async Task<string> PendingRequestId()
    {
        await SeedEncryptedContext(ConversationId, ConversationId, OwnerId);
        await Submit(ProtectionLevel.TrustedSignIn);
        return (await _context.MlsJoinRequests.SingleAsync()).Id;
    }

    private Task<MlsOperationResult> IssueChallenge(string requestId, byte[]? nonce = null,
        DateTimeOffset? at = null) =>
        _service.IssueChallengeAsync(ConversationId, requestId, OwnerId, "device-owner",
            new IssueAdmissionChallengeDto { Challenge = nonce ?? Nonce }, at ?? T0);

    [Test]
    public async Task Challenge_MustBeThirtyTwoBytes()
    {
        var requestId = await PendingRequestId();

        var result = await IssueChallenge(requestId, nonce: [1, 2, 3]);

        // A client must not be able to weaken its own challenge and have the server carry it anyway.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.BadRequest));
    }

    [Test]
    public async Task Challenge_CannotBeIssuedByTheDeviceBeingAdmitted()
    {
        var requestId = await PendingRequestId();

        var result = await _service.IssueChallengeAsync(ConversationId, requestId, RequesterId, "device-new",
            new IssueAdmissionChallengeDto { Challenge = Nonce }, T0);

        // The whole point is that a *different* device, one already trusted with the master key,
        // chose the nonce. Challenging yourself proves nothing.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.BadRequest));
    }

    [Test]
    public async Task Proof_IsRelayedVerbatimAndNeverValidated()
    {
        var requestId = await PendingRequestId();
        var challenge = (MlsAdmissionChallengeDto)(await IssueChallenge(requestId)).Value!;

        // Deliberately nonsense.
        var submitted = await _service.SubmitProofAsync(ConversationId, requestId, RequesterId,
            new SubmitAdmissionProofDto { ChallengeId = challenge.ChallengeId, Proof = [0xDE, 0xAD] }, T0);

        Assert.That(submitted.Status, Is.EqualTo(MlsOperationStatus.Ok));

        var relayed = (MlsAdmissionProofDto)(await _service.GetProofAsync(ConversationId, requestId, T0)).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(relayed.Proof, Is.EqualTo(new byte[] { 0xDE, 0xAD }));
            // The verifier gets the nonce and the signed-over values too, so it never has to trust
            // the server's account of what was signed.
            Assert.That(relayed.Challenge, Is.EqualTo(Nonce));
            Assert.That(relayed.RequesterDeviceId, Is.EqualTo("device-new"));
            Assert.That(relayed.SignatureKeyFingerprint, Is.EqualTo("AAAA-BBBB-CCCC"));
        });
    }

    [Test]
    public async Task Proof_CanOnlyBeSubmittedOnce()
    {
        var requestId = await PendingRequestId();
        var challenge = (MlsAdmissionChallengeDto)(await IssueChallenge(requestId)).Value!;
        var dto = new SubmitAdmissionProofDto { ChallengeId = challenge.ChallengeId, Proof = [1] };

        await _service.SubmitProofAsync(ConversationId, requestId, RequesterId, dto, T0);
        var second = await _service.SubmitProofAsync(ConversationId, requestId, RequesterId,
            new SubmitAdmissionProofDto { ChallengeId = challenge.ChallengeId, Proof = [2] }, T0);

        // Two different signatures over one nonce means at least one did not come from the device
        // that should have made it. Quietly keeping the later one would hide exactly that.
        Assert.That(second.Status, Is.EqualTo(MlsOperationStatus.Conflict));
        Assert.That((await _context.MlsAdmissionChallenges.SingleAsync()).Proof, Is.EqualTo(new byte[] { 1 }));
    }

    [Test]
    public async Task Proof_CannotBeSubmittedAfterFifteenMinutes()
    {
        var requestId = await PendingRequestId();
        var challenge = (MlsAdmissionChallengeDto)(await IssueChallenge(requestId)).Value!;

        var result = await _service.SubmitProofAsync(ConversationId, requestId, RequesterId,
            new SubmitAdmissionProofDto { ChallengeId = challenge.ChallengeId, Proof = [1] },
            T0 + MlsAdmissionChallenge.Lifetime + TimeSpan.FromSeconds(1));

        // A proof that stays valid indefinitely turns one intercepted signature into a permanent
        // admission ticket.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
    }

    [Test]
    public async Task Proof_ExpiresOutOfTheRelayEvenOnceSubmitted()
    {
        var requestId = await PendingRequestId();
        var challenge = (MlsAdmissionChallengeDto)(await IssueChallenge(requestId)).Value!;
        await _service.SubmitProofAsync(ConversationId, requestId, RequesterId,
            new SubmitAdmissionProofDto { ChallengeId = challenge.ChallengeId, Proof = [1] }, T0);

        var late = await _service.GetProofAsync(ConversationId, requestId,
            T0 + MlsAdmissionChallenge.Lifetime + TimeSpan.FromSeconds(1));

        Assert.That(late.Status, Is.EqualTo(MlsOperationStatus.NotFound));
    }

    [Test]
    public async Task Challenge_IsOnlyReadableByTheRequester()
    {
        var requestId = await PendingRequestId();
        await IssueChallenge(requestId);

        var result = await _service.GetChallengeAsync(ConversationId, requestId, "somebody-else", T0);

        // Handing the nonce to anyone else is inviting them to try to answer it.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.NotFound));
    }

    [Test]
    public async Task Proof_CannotBeSubmittedByAnotherUser()
    {
        var requestId = await PendingRequestId();
        var challenge = (MlsAdmissionChallengeDto)(await IssueChallenge(requestId)).Value!;

        var result = await _service.SubmitProofAsync(ConversationId, requestId, "attacker",
            new SubmitAdmissionProofDto { ChallengeId = challenge.ChallengeId, Proof = [1] }, T0);

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.NotFound));
        Assert.That((await _context.MlsAdmissionChallenges.SingleAsync()).Proof, Is.Null);
    }

    [Test]
    public async Task IssuingASecondChallenge_SupersedesTheFirst()
    {
        var requestId = await PendingRequestId();
        await IssueChallenge(requestId);
        await IssueChallenge(requestId, nonce: Enumerable.Repeat((byte)9, 32).ToArray());

        // Two outstanding nonces means a reviewer cannot tell which proof it is looking at, and
        // gives an attacker two chances at one interception.
        var live = await _context.MlsAdmissionChallenges.SingleAsync();
        Assert.That(live.Challenge, Is.EqualTo(Enumerable.Repeat((byte)9, 32).ToArray()));
    }
}
