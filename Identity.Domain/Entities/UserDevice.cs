using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;


public class CreateUserDeviceParams
{
    public string ClientDeviceId { get; set; }
    public string DeviceName { get; set; }
    public DeviceType DeviceType { get; set; }
    public byte[] IdentityPublicKey { get; set; }
    public string UserId { get; set; }
}

public class UserDevice  : BaseEntity<UserDevice>, IPrefixedEntity
{
    public static string Prefix { get; } = "udev";
    
    public string ClientDeviceId { get; init; }
    
    public string UserId { get; set; } = null!;
    public string DeviceName { get; set; } = null!;      // "Alice's MacBook"
    public DeviceType DeviceType { get; set; }            // DESKTOP, MOBILE, WEB
    public byte[] IdentityPublicKey { get; set; } = null!; // MLS public identity key
    // private never leaves device
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public DateTimeOffset? LastSeen { get; set; }

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public ICollection<UserKeyPackage> KeyPackages { get; set; } = [];
    public virtual UserDeviceBackup? Backup { get; set; }
    
    public static UserDevice Create(CreateUserDeviceParams createUserDeviceParams)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;
        return new UserDevice
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            ClientDeviceId = createUserDeviceParams.ClientDeviceId,
            DeviceName = createUserDeviceParams.DeviceName,
            DeviceType = createUserDeviceParams.DeviceType,
            IdentityPublicKey = createUserDeviceParams.IdentityPublicKey,
            UserId = createUserDeviceParams.UserId,
        };
    }

}