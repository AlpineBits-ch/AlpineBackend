using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Aggregates;
using Persistence;

namespace Identity.Domain.Entities;

public class UserDeviceToken : BaseEntity<UserDeviceToken>, IPrefixedEntity
{
    
    public string Token { get; set; }
    public string UserId { get; set; }
    public virtual ApplicationUser User { get; set; }
    [NotMapped] public static string Prefix { get; } = "udtk";
}