using Domain;

namespace Identity.Application.Dtos.Request;

public class ProtectionLevelDto
{
    public ProtectionLevel Level { get; set; }

    /// <summary>Signed by the user's identity key.</summary>
    public byte[]? SignedAssertion { get; set; }

    public int Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public class PutProtectionLevelDto
{
    public ProtectionLevel Level { get; set; }

    /// <summary>Required. The assertion the clients will actually enforce.</summary>
    public byte[] SignedAssertion { get; set; } = null!;

    /// <summary>Must be exactly the stored version plus one.</summary>
    public int Version { get; set; }

    /// <summary>Account password.</summary>
    public string? Password { get; set; }

    /// <summary>Client device id making the change, recorded in the audit row and echoed in the
    /// broadcast so the user's other devices can tell "I did this" from "something did this".</summary>
    public string? DeviceId { get; set; }
}

/// <summary>Why the strict tier could not be turned on, in terms the UI can put in front of a
/// person. "You cannot enable this" with no reason is how a security setting ends up unused.</summary>
public class ProtectionLevelUpgradeBlockedDto
{
    public List<string> BlockingDeviceNames { get; set; } = new();
    public List<string> Reasons { get; set; } = new();
}
