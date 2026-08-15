using AppEnvironment;
using Echo.Entitlements.Wire;
using Echo.Voice.Rooms;
using Guild.Domain.Enums;

namespace Guild.Application.Services;

/// <summary>
/// The two answers a voice limit needs before it can be described to a client, and which neither
/// the room nor the client can supply: whether this instance sells anything at all, and whether
/// this particular caller could buy the guild's upgrade.
/// </summary>
public static class GuildVoiceRemedies
{
    /// <summary>False on a self-hosted instance and on a hosted one whose billing is not configured.
    /// Both render as a sentence with no button, because an upgrade link to a service that is not
    /// deployed is worse than no link.</summary>
    public static bool InstanceSellsUpgrades => Env.License.IsHosted && Env.License.IsBillingConfigured;

    /// <summary>The join's answer. A capacity degradation is the only thing a join can produce.</summary>
    public static Task<bool> ActorCanRemedyAsync(
        GuildPermissionService permissions, string userId, string guildId, VoiceAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        return ActorCanRemedyAsync(
            permissions, userId, guildId,
            admission.OverCapacity is null ? [] : [admission.OverCapacity]);
    }

    /// <summary>The publish's answer, over the refusal if there is one and the reductions otherwise.
    /// One or the other, never both: a refused publish carries no reductions, because nothing was
    /// reduced.</summary>
    public static Task<bool> ActorCanRemedyAsync(
        GuildPermissionService permissions, string userId, string guildId, VoicePublishDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return ActorCanRemedyAsync(
            permissions, userId, guildId,
            decision.Refusal is null ? decision.Degradations : [decision.Refusal]);
    }

    /// <summary>
    /// Whether this caller holds <c>ManageGuild</c>, asked only when the answer could change
    /// anything.
    /// </summary>
    private static async Task<bool> ActorCanRemedyAsync(
        GuildPermissionService permissions,
        string userId,
        string guildId,
        IReadOnlyList<VoiceDegradation> degradations)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (!InstanceSellsUpgrades) return false;
        if (!degradations.Any(d => d.Cause.BoundBy == EntitlementBoundBy.Guild)) return false;

        return await permissions.CanUserPerformActionOnGuildAsync(
            userId, guildId, Permissions.ManageGuild);
    }
}
