using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

public class UserPreferences : BaseEntity<UserPreferences>, IPrefixedEntity
{
    public Theme Theme { get; set; } = Theme.System;

    /// <summary>Superseded by <see cref="UserPrivacySettings.DirectMessagePolicy"/>.</summary>
    [Obsolete("Superseded by UserPrivacySettings.DirectMessagePolicy. Read/write api/v1/privacy-settings instead; this column is retained only so v1 clients keep parsing GET /users/self.")]
    public DirectMessageSettings DirectMessageSettings { get; set; } = DirectMessageSettings.FilterNonFriends;

    /// <summary>
    /// Superseded by the explicit consent columns on <see cref="UserPrivacySettings"/>
    /// (<c>AllowDataCollection</c>, <c>AllowPersonalization</c>,
    /// <c>AllowVoiceRecordingInClips</c>).
    /// </summary>
    [Obsolete("Superseded by UserPrivacySettings.AllowDataCollection/AllowPersonalization/AllowVoiceRecordingInClips. Read/write api/v1/privacy-settings instead; this column is retained only so v1 clients keep parsing GET /users/self.")]
    public PrivacySettings PrivacySettings { get; set; } = PrivacySettings.None;

    public string Data { get; set; } = "{}";
    [NotMapped] public static string Prefix { get; } = "upre";

}
