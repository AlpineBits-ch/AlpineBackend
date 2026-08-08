namespace Messaging.Application.Dtos.Response;

/// <summary>
/// Which devices hold a leaf in a context's live MLS group, read on demand rather than only in the
/// response to the write that formed it.
/// </summary>
public class MlsCoverageDto
{
    public string ContextId { get; set; } = null!;

    /// <summary>False when the context has no live group.</summary>
    public bool Encrypted { get; set; }

    /// <summary>The generation coverage was computed against, or null when encryption is off. A
    /// device covered in generation 2 is not covered in generation 3, so an answer without this is
    /// unreadable.</summary>
    public int? Generation { get; set; }

    /// <summary>Every active device of the caller, covered or not.</summary>
    public List<MlsDeviceCoverageDto> OwnDevices { get; set; } = new();

    /// <summary>Other members' devices that hold no leaf.</summary>
    public List<UnreachableDeviceDto> UnreachableDevices { get; set; } = new();

    /// <summary>
    /// True when the device list could not be read at all, so both lists are empty because nothing
    /// could be looked up.
    /// </summary>
    public bool CoverageUnavailable { get; set; }
}

/// <summary>One of the caller's devices and whether it can read the live group.</summary>
public class MlsDeviceCoverageDto
{
    /// <summary>Client device id, so a client can match the entry against the one it is running on.</summary>
    public string DeviceId { get; set; } = null!;

    public string DeviceName { get; set; } = null!;

    /// <summary>
    /// True when the server can see this device holds a leaf: a Welcome was addressed to it, it
    /// published a commit, or it is the device that built the group.
    /// </summary>
    public bool Covered { get; set; }
}
