using System.Text;
using System.Text.Json;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Services;

/// <summary>Covers T2-21's enforcement point.</summary>
[TestFixture]
public class VoiceRecordingConsentTests
{
    private const string SessionId = "call-1";

    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp() => _cache = new FakeDistributedCache();

    /// <summary>A store that grants exactly the named users for any session - what a real clip
    /// feature's per-session prompt would populate.</summary>
    private sealed class StubSessionConsentStore(params string[] granted) : IVoiceRecordingSessionConsentStore
    {
        public Task<IReadOnlySet<string>> GetGrantedAsync(
            string sessionId, IReadOnlyCollection<string> participantUserIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(granted.ToHashSet(StringComparer.Ordinal));
    }

    private sealed class ThrowingSessionConsentStore : IVoiceRecordingSessionConsentStore
    {
        public Task<IReadOnlySet<string>> GetGrantedAsync(
            string sessionId, IReadOnlyCollection<string> participantUserIds, CancellationToken ct = default) =>
            throw new InvalidOperationException("consent store is down");
    }

    private void SeedCall(params string[] connectedUserIds)
    {
        var call = new Call
        {
            Id = SessionId,
            ConversationId = "conv-1",
            CreatorId = connectedUserIds.FirstOrDefault() ?? "user-1",
            Status = CallStatus.Connected,
            Participants = connectedUserIds
                .Select(id => new CallParticipant { UserId = id, Status = CallStatus.Connected })
                .ToList(),
        };

        _cache.SetEntry(Call.GetCacheId(SessionId), JsonSerializer.Serialize(call));
    }

    private static FakeMessageBus PrivacyBus(bool lookupFails = false, params (string UserId, bool Allows)[] flags)
    {
        var byId = flags.ToDictionary(f => f.UserId, f => f.Allows, StringComparer.Ordinal);

        return new FakeMessageBus(message => message switch
        {
            GetUserPrivacySettingsRequest when lookupFails =>
                throw new InvalidOperationException("identity is down"),

            GetUserPrivacySettingsRequest r => new GetUserPrivacySettingsResponse
            {
                Settings = r.UserIds
                    .Where(byId.ContainsKey)
                    .Select(id => TestPrivacyServices.With(id, s => s.AllowVoiceRecordingInClips = byId[id]))
                    .ToList(),
            },

            _ => throw new InvalidOperationException($"unexpected {message.GetType().Name}"),
        });
    }

    private IVoiceRecordingConsent Build(
        FakeMessageBus bus, IVoiceRecordingSessionConsentStore? store = null) =>
        new VoiceRecordingConsent(_cache, store ?? new DeniedByDefaultSessionConsentStore(), bus,
            NullLogger<VoiceRecordingConsent>.Instance);

    // ── normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task Evaluate_EveryParticipantConsentsForThisSession_Allows()
    {
        SeedCall("user-1", "user-2");
        var consent = Build(
            PrivacyBus(flags: [("user-1", true), ("user-2", true)]),
            new StubSessionConsentStore("user-1", "user-2"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1", "user-2"]);

        Assert.That(decision.Allowed, Is.True);
    }

    // ── edge ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Evaluate_NoSessionOrNoParticipants_IsMalformed()
    {
        var consent = Build(PrivacyBus());

        Assert.Multiple(async () =>
        {
            var noSession = await consent.EvaluateAsync(null, ["user-1"]);
            var noParticipants = await consent.EvaluateAsync(SessionId, []);

            Assert.That(noSession.Reason, Is.EqualTo(VoiceRecordingRefusal.MalformedRequest));
            Assert.That(noParticipants.Reason, Is.EqualTo(VoiceRecordingRefusal.MalformedRequest));
        });
    }

    [Test]
    public async Task Evaluate_ParticipantWhoIsNotInTheSession_IsRefusedWithoutAskingIdentity()
    {
        // Otherwise the caller picks both the "session" and the people, and the check degenerates
        // into the account-level read this type exists to reject - and doubles as a probe of any
        // user's settings.
        SeedCall("user-1");
        var bus = PrivacyBus(flags: [("user-1", true), ("stranger", true)]);
        var consent = Build(bus, new StubSessionConsentStore("user-1", "stranger"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1", "stranger"]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.UnknownSession));
            Assert.That(bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task Evaluate_ParticipantRingingButNotYetConnected_IsRefused()
    {
        var call = new Call
        {
            Id = SessionId,
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Status = CallStatus.Connected,
            Participants =
            [
                new CallParticipant { UserId = "user-1", Status = CallStatus.Connected },
                new CallParticipant { UserId = "user-2", Status = CallStatus.Pending },
            ],
        };
        _cache.SetEntry(Call.GetCacheId(SessionId), JsonSerializer.Serialize(call));
        var consent = Build(
            PrivacyBus(flags: [("user-1", true), ("user-2", true)]),
            new StubSessionConsentStore("user-1", "user-2"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1", "user-2"]);

        Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.UnknownSession));
    }

    [Test]
    public async Task Evaluate_EndedSession_IsRefused()
    {
        var call = new Call
        {
            Id = SessionId,
            ConversationId = "conv-1",
            CreatorId = "user-1",
            Status = CallStatus.Completed,
            Participants = [new CallParticipant { UserId = "user-1", Status = CallStatus.Connected }],
        };
        _cache.SetEntry(Call.GetCacheId(SessionId), JsonSerializer.Serialize(call));
        var consent = Build(PrivacyBus(flags: [("user-1", true)]), new StubSessionConsentStore("user-1"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1"]);

        Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.UnknownSession));
    }

    // ── negative (the point) ──────────────────────────────────────────────

    [Test]
    public async Task Evaluate_AsShipped_RefusesEverything()
    {
        // No clip feature exists, so no per-session grant can exist either.
        SeedCall("user-1", "user-2");
        var consent = Build(PrivacyBus(flags: [("user-1", true), ("user-2", true)]));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1", "user-2"]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.NoSessionConsent));
            Assert.That(decision.RefusedUserIds, Is.EquivalentTo(new[] { "user-1", "user-2" }));
        });
    }

    [Test]
    public async Task Evaluate_AccountFlagOnButNoSessionGrant_IsStillRefused()
    {
        // The whole of T2-21: an account-level flag is not consent to record this conversation.
        SeedCall("user-1", "user-2");
        var consent = Build(
            PrivacyBus(flags: [("user-1", true), ("user-2", true)]),
            new StubSessionConsentStore("user-1"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1", "user-2"]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.NoSessionConsent));
            Assert.That(decision.RefusedUserIds, Is.EquivalentTo(new[] { "user-2" }));
        });
    }

    [Test]
    public async Task Evaluate_OneParticipantHasTheFlagOff_RefusesTheWholeRecording()
    {
        // Not "record everyone else": a clip of a conversation contains the conversation, and the
        // refusing participant's voice is in it whether or not their track is the one being written.
        SeedCall("user-1", "user-2");
        var consent = Build(
            PrivacyBus(flags: [("user-1", true), ("user-2", false)]),
            new StubSessionConsentStore("user-1", "user-2"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1", "user-2"]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.ParticipantOptedOut));
            Assert.That(decision.RefusedUserIds, Is.EquivalentTo(new[] { "user-2" }));
        });
    }

    [Test]
    public async Task Evaluate_IdentityUnreachable_Refuses()
    {
        SeedCall("user-1");
        var consent = Build(PrivacyBus(lookupFails: true), new StubSessionConsentStore("user-1"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1"]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.Unresolved));
        });
    }

    [Test]
    public async Task Evaluate_IdentityAnswersWithoutOneParticipant_Refuses()
    {
        // "Identity did not mention them" is not "they said yes".
        SeedCall("user-1", "user-2");
        var consent = Build(
            PrivacyBus(flags: [("user-1", true)]),
            new StubSessionConsentStore("user-1", "user-2"));

        var decision = await consent.EvaluateAsync(SessionId, ["user-1", "user-2"]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.Unresolved));
            Assert.That(decision.RefusedUserIds, Is.EquivalentTo(new[] { "user-2" }));
        });
    }

    [Test]
    public async Task Evaluate_SessionStoreUnreadable_Refuses()
    {
        SeedCall("user-1");
        var consent = Build(PrivacyBus(flags: [("user-1", true)]), new ThrowingSessionConsentStore());

        var decision = await consent.EvaluateAsync(SessionId, ["user-1"]);

        Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.Unresolved));
    }

    [Test]
    public async Task Evaluate_UnknownSession_Refuses()
    {
        var consent = Build(PrivacyBus(), new StubSessionConsentStore("user-1"));

        var decision = await consent.EvaluateAsync("call-does-not-exist", ["user-1"]);

        Assert.That(decision.Reason, Is.EqualTo(VoiceRecordingRefusal.UnknownSession));
    }

    [Test]
    public async Task CanRecordAsync_MirrorsEvaluate()
    {
        SeedCall("user-1");
        var consent = Build(PrivacyBus(flags: [("user-1", true)]));

        Assert.That(await consent.CanRecordAsync(SessionId, ["user-1"]), Is.False);
    }

    [Test]
    public async Task DeniedByDefaultSessionConsentStore_GrantsNothing()
    {
        var store = new DeniedByDefaultSessionConsentStore();

        var granted = await store.GetGrantedAsync(SessionId, ["user-1", "user-2"]);

        Assert.That(granted, Is.Empty);
    }
}
