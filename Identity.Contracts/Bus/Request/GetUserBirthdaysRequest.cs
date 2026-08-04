namespace Identity.Contracts.Bus.Request;

/// <summary>
/// "What is each of these accounts' date of birth?" - Identity owns it (privacy spec T2-17), Social
/// renders it behind <c>BirthdayVisibility</c>.
///
/// <para><b>Batched by design</b>, exactly like <see cref="GetUserPrivacySettingsRequest"/>: today's
/// only caller projects one profile at a time, but a member-list projection would otherwise turn
/// into N sequential bus calls, which is the shape that made <c>ConversationEndpoints</c> slow.</para>
///
/// <para><b>This is the most sensitive field the profile surface carries.</b> A full date of birth is
/// identity-theft-grade on its own and it is what drives the minor floors, so the handler answering
/// this request refuses outright for any account whose <c>BirthdayVisibility</c> is
/// <c>Visibility.Nobody</c> - the shipped default. That refusal is a viewer-independent floor, not
/// the gate: the gate is per-viewer and lives in Social's <c>ProfileProjectionService</c>, because
/// Identity does not know who is asking on whose behalf.</para>
/// </summary>
public class GetUserBirthdaysRequest
{
    public ICollection<string> UserIds { get; set; } = new List<string>();
}
