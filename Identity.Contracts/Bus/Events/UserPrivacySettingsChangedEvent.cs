namespace Identity.Contracts.Bus.Events;

/// <summary>
/// An account's privacy record was written.
///
/// <para><b>Deliberately carries no values.</b> It is a cache-eviction signal, not a replication
/// feed: every consuming service keeps a Redis-backed <c>PrivacySettingsCache</c> keyed
/// <c>privacy_settings:user_id:{id}</c>, and the correct reaction to this event is to drop the key
/// and re-ask. Shipping the values instead would put a user's contactability policy on a bus
/// topic every service can subscribe to, and would make two events delivered out of order silently
/// resurrect a setting the user just turned off.</para>
///
/// <para><see cref="Version"/> is included so a consumer that caches the version alongside the
/// values can tell an event about a write it already has from one about a write it does not.</para>
/// </summary>
public class UserPrivacySettingsChangedEvent
{
    public string UserId { get; set; } = null!;

    /// <summary>The value of <c>UserPrivacySettings.Version</c> <i>after</i> the write.</summary>
    public int Version { get; set; }
}
