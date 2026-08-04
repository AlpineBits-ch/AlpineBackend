namespace Identity.Contracts.Bus.Events;

/// <summary>An account's privacy record was written.</summary>
public class UserPrivacySettingsChangedEvent
{
    public string UserId { get; set; } = null!;

    /// <summary>The value of <c>UserPrivacySettings.Version</c> after the write.</summary>
    public int Version { get; set; }
}
