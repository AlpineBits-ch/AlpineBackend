namespace Guild.Domain.Enums;

/// <summary>Mirrors Discord's verification-level concept - gates invite redemption (joining) on
/// how established the joining account is. Applied at join time only in v1 (see project docs);
/// does not retroactively restrict members who already met the bar when they joined but whose
/// account "ages down" is not a real scenario, so no ongoing re-check is needed.</summary>
public enum GuildVerificationLevel
{
    None,
    Low,
    Medium,
    High,
}

public static class GuildVerificationLevelExtensions
{
    private static readonly TimeSpan MediumMinAccountAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HighMinAccountAge = TimeSpan.FromMinutes(10);

    /// <summary>Pure decision function, extracted out of InviteEndpoint specifically so the
    /// join-gating rules are unit-testable without needing a full invite-redemption fixture.</summary>
    public static bool MeetsRequirement(this GuildVerificationLevel level, bool emailConfirmed, TimeSpan accountAge) =>
        level switch
        {
            GuildVerificationLevel.Low => emailConfirmed,
            GuildVerificationLevel.Medium => emailConfirmed && accountAge >= MediumMinAccountAge,
            GuildVerificationLevel.High => emailConfirmed && accountAge >= HighMinAccountAge,
            _ => true,
        };
}
