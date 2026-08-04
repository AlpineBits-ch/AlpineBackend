using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>The answer to "may this recording start?", plus who refused and why.</summary>
/// <param name="Allowed">True only when every named participant may be recorded.</param>
/// <param name="RefusedUserIds">The participants that blocked it. Empty when
/// <paramref name="Allowed"/> is true, and empty as well when the refusal is not about a particular
/// person (an unresolvable session, say).</param>
/// <param name="Reason">A machine-readable code for the refusal, or <c>null</c> when allowed.</param>
public sealed record VoiceRecordingDecision(
    bool Allowed,
    IReadOnlyCollection<string> RefusedUserIds,
    string? Reason)
{
    public static VoiceRecordingDecision Allow() => new(true, [], null);

    public static VoiceRecordingDecision Refuse(string reason, IEnumerable<string>? refusedUserIds = null) =>
        new(false, refusedUserIds?.ToArray() ?? [], reason);
}

/// <summary>Refusal codes. Stable strings, because a caller that has to distinguish "this person
/// said no" from "we could not tell" will otherwise parse the message.</summary>
public static class VoiceRecordingRefusal
{
    /// <summary>Nothing identifiable was asked about - no session, or no participants.</summary>
    public const string MalformedRequest = "recording_request_malformed";

    /// <summary>The session does not exist, is not live, or does not contain everyone named.</summary>
    public const string UnknownSession = "recording_session_unresolved";

    /// <summary>At least one participant has <c>AllowVoiceRecordingInClips</c> off.</summary>
    public const string ParticipantOptedOut = "recording_participant_opted_out";

    /// <summary>At least one participant has not granted consent <i>for this session</i>.</summary>
    public const string NoSessionConsent = "recording_no_session_consent";

    /// <summary>The consent could not be resolved at all. Refused, never assumed.</summary>
    public const string Unresolved = "recording_consent_unresolved";
}

/// <summary>
/// T2-21's enforcement point: may a clip be recorded from this voice session, capturing these
/// participants, right now?
///
/// <para><b>An account-level flag is not consent to record other people.</b>
/// <c>AllowVoiceRecordingInClips</c> is a standing refusal - "never record me" - and nothing more.
/// It cannot be consent, for two reasons. It is not addressed to anyone: a user who set it while
/// signing up did not agree to be recorded by whoever happens to be in a call with them eleven
/// months later. And it is not addressed to an occasion: consent to be recorded in one conversation
/// says nothing about the next one, which is precisely the property that makes recording a person
/// something they get to decide each time. So the flag being <c>true</c> is a <i>necessary</i>
/// condition here, never a sufficient one.</para>
///
/// <para>What this interface therefore requires is <b>per session, per participant, evaluated at
/// record time</b>:</para>
/// <list type="number">
///   <item>the session must exist, be live, and actually contain every person named - you cannot
///   consent on behalf of someone who is not there, and a caller naming arbitrary user ids must not
///   be able to probe their settings;</item>
///   <item>every named participant must have the account-level flag on;</item>
///   <item>every named participant must additionally have granted consent for <i>this</i> session,
///   recorded through <see cref="IVoiceRecordingSessionConsentStore"/>.</item>
/// </list>
///
/// <para><b>There is no clip feature today, and this is not one.</b> The spec's requirement is that
/// the enforcement point exists and is closed, so the feature cannot ship without honouring consent
/// - the shipped <see cref="DeniedByDefaultSessionConsentStore"/> grants nothing, so
/// <see cref="EvaluateAsync"/> currently refuses every request. Building the clip feature means
/// registering a store that records real per-session grants (a prompt each participant answers when
/// recording is proposed) and calling <see cref="EvaluateAsync"/> at the point capture begins -
/// before the first byte is written, not before the clip is published.</para>
///
/// <para><b>Fails closed.</b> Any lookup that cannot be completed - Identity unreachable, session
/// store unreadable, a participant Identity does not return - refuses. Recording is not a path
/// where "we could not check" may become "go ahead".</para>
/// </summary>
public interface IVoiceRecordingConsent
{
    /// <summary>Evaluates the full decision, including who refused.</summary>
    Task<VoiceRecordingDecision> EvaluateAsync(
        string? sessionId, IReadOnlyCollection<string>? participantUserIds, CancellationToken ct = default);

    /// <summary>The same decision as a bare boolean, for call sites that only branch on it.</summary>
    async Task<bool> CanRecordAsync(
        string? sessionId, IReadOnlyCollection<string>? participantUserIds, CancellationToken ct = default) =>
        (await EvaluateAsync(sessionId, participantUserIds, ct)).Allowed;
}

/// <summary>
/// Where a per-session, per-participant recording grant is looked up.
///
/// <para>Separate from <see cref="IVoiceRecordingConsent"/> because the two answer different
/// questions and only one of them is a feature: "has this person, in this session, just now agreed
/// to be recorded" is state a clip feature must capture and store, while "may this recording start"
/// is a rule that holds whether or not that feature exists. Splitting them is what lets the rule
/// ship first and stay closed.</para>
/// </summary>
public interface IVoiceRecordingSessionConsentStore
{
    /// <summary>The subset of <paramref name="participantUserIds"/> that have an active,
    /// unexpired grant for <paramref name="sessionId"/>. Implementations must return only grants
    /// they can positively confirm - an unreachable backing store returns nothing, never
    /// everything.</summary>
    Task<IReadOnlySet<string>> GetGrantedAsync(
        string sessionId, IReadOnlyCollection<string> participantUserIds, CancellationToken ct = default);
}

/// <summary>
/// The registered default: nobody has ever granted anything.
///
/// <para>Which means every recording is refused, which is the correct behaviour for a product with
/// no clip feature and no consent-capture UI. Swapping this one registration - for a store fed by a
/// real per-session prompt - is the whole of enabling recording, and there is deliberately no way
/// to enable it that skips the grant.</para>
/// </summary>
public sealed class DeniedByDefaultSessionConsentStore : IVoiceRecordingSessionConsentStore
{
    public Task<IReadOnlySet<string>> GetGrantedAsync(
        string sessionId, IReadOnlyCollection<string> participantUserIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// The shipped <see cref="IVoiceRecordingConsent"/>. Resolves the call session from the same
/// distributed cache <c>VoiceController</c> reads, then evaluates every participant independently.
///
/// <para>The privacy lookup here goes straight to Identity rather than through
/// <see cref="PrivacySettingsCache"/>, and that is deliberate: everywhere else in Messaging a
/// five-minute-stale answer costs at most one wrongly-delivered DM, whereas here it would record a
/// person who has already withdrawn. Recording starts rarely enough to afford the round trip, and
/// "at record time" in the spec means exactly that. A failed lookup refuses.</para>
/// </summary>
public sealed class VoiceRecordingConsent(
    IDistributedCache cache,
    IVoiceRecordingSessionConsentStore sessionConsent,
    IMessageBus bus,
    ILogger<VoiceRecordingConsent> logger) : IVoiceRecordingConsent
{
    public async Task<VoiceRecordingDecision> EvaluateAsync(
        string? sessionId, IReadOnlyCollection<string>? participantUserIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.MalformedRequest);

        var participants = (participantUserIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (participants.Count == 0)
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.MalformedRequest);

        // 1. Ground the request in a real session. Without this the caller chooses both the
        //    "session" and the people, and the check degenerates into the account-level read this
        //    type exists to reject.
        Call? call;
        try
        {
            call = await CallService.GetCallById(sessionId, cache);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read call session {SessionId} while evaluating recording consent", sessionId);
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.Unresolved);
        }

        if (call is null || call.Status is CallStatus.Completed or CallStatus.Rejected)
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.UnknownSession);

        var connected = call.Participants
            .Where(p => p.Status == CallStatus.Connected)
            .Select(p => p.UserId)
            .ToHashSet(StringComparer.Ordinal);

        if (participants.Any(id => !connected.Contains(id)))
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.UnknownSession);

        // 2. The standing account-level refusal. Necessary, not sufficient.
        Dictionary<string, bool> allowsRecording;
        try
        {
            var response = await bus.InvokeAsync<GetUserPrivacySettingsResponse>(
                new GetUserPrivacySettingsRequest { UserIds = participants }, ct);

            allowsRecording = (response?.Settings ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.UserId))
                .GroupBy(s => s.UserId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().AllowVoiceRecordingInClips, StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Privacy lookup failed while evaluating recording consent for session {SessionId}", sessionId);
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.Unresolved);
        }

        var unresolved = participants.Where(id => !allowsRecording.ContainsKey(id)).ToList();
        if (unresolved.Count > 0)
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.Unresolved, unresolved);

        var optedOut = participants.Where(id => !allowsRecording[id]).ToList();
        if (optedOut.Count > 0)
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.ParticipantOptedOut, optedOut);

        // 3. The per-session grant. This is the one that makes the check consent rather than a
        //    preference read, and the shipped store grants nothing.
        IReadOnlySet<string> granted;
        try
        {
            granted = await sessionConsent.GetGrantedAsync(sessionId, participants, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Session consent lookup failed for session {SessionId}", sessionId);
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.Unresolved);
        }

        var ungranted = participants.Where(id => !granted.Contains(id)).ToList();
        if (ungranted.Count > 0)
            return VoiceRecordingDecision.Refuse(VoiceRecordingRefusal.NoSessionConsent, ungranted);

        return VoiceRecordingDecision.Allow();
    }
}
