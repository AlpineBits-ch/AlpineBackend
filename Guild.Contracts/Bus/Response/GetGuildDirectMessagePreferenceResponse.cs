namespace Guild.Contracts.Bus.Response;

public class GetGuildDirectMessagePreferenceResponse
{
    public ICollection<GuildDirectMessagePreferenceSummary> Preferences { get; set; } =
        new List<GuildDirectMessagePreferenceSummary>();
}

/// <summary>
/// The <i>effective</i> answer for one guild, not the raw row: a user with no stored override for a
/// guild still appears here, carrying the value derived from their global
/// <c>DirectMessagePolicy</c>. A caller must never have to know that the override table exists, and
/// must never read "absent" as "allowed".
///
/// <para>When Guild cannot reach Identity to learn the global policy and there is no stored row, the
/// value is <c>false</c>. Fail closed - see the cross-cutting rules in docs/specs/privacy.md.</para>
/// </summary>
public class GuildDirectMessagePreferenceSummary
{
    public string GuildId { get; set; } = null!;

    public bool AllowDirectMessages { get; set; }
}
