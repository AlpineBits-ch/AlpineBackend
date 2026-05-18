using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

public class UserPreferences : BaseEntity<UserPreferences>, IPrefixedEntity
{
    public Theme Theme { get; set; } = Theme.System;
    public DirectMessageSettings DirectMessageSettings { get; set; } = DirectMessageSettings.FilterNonFriends;
    public PrivacySettings PrivacySettings { get; set; } = PrivacySettings.None;
    public string Data { get; set; } = "{}";
    [NotMapped] public static string Prefix { get; } = "upre";

}