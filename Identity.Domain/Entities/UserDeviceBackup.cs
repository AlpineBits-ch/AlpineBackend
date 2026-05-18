using Identity.Domain.Aggregates;
using Persistence;

namespace Identity.Domain.Entities;

public class UserDeviceBackup : BaseEntity<UserDeviceBackup>, IPrefixedEntity
{
    public static string Prefix { get; } = "udba";
    public string UserId { get; set; }
    public virtual ApplicationUser User { get; set; }
    public string DeviceId { get; set; }
    public virtual UserDevice Device { get; set; }
    public byte[] Backup { get; set; }

    public override string ToString()
    {
        return $"{Prefix}:{UserId}:{DeviceId}";
    }
}