using Facet;
using Messaging.Application.Dtos.Response;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;

namespace Messaging.Application.Dtos.Request;

/// <summary>Turns encryption on for a context.</summary>
public class EnableMlsDto
{
    /// <summary>Opaque MLS group id bytes the client generated.</summary>
    public byte[] MlsGroupId { get; set; } = null!;

    /// <summary>Epoch the group is on after the initial adds.</summary>
    public long Epoch { get; set; }

    /// <summary>GroupInfo for external-commit recovery. Optional but strongly recommended.</summary>
    public byte[]? MlsGroupInfo { get; set; }

    /// <summary>One Welcome per device that was added to the new group.</summary>
    public List<DeviceWelcomeDto> Welcomes { get; set; } = new();
}

/// <summary>Current encryption state of a context, plus every generation it has ever had.</summary>
public class MlsContextStateDto
{
    public string ContextId { get; set; } = null!;

    public bool Encrypted { get; set; }

    /// <summary>Null when encryption is off.</summary>
    public int? ActiveGeneration { get; set; }

    public long? Epoch { get; set; }
    public byte[]? MlsGroupId { get; set; }

    /// <summary>
    /// The live GroupInfo, or null when the caller has no evidence of ever having been in this
    /// group.
    /// </summary>
    public byte[]? MlsGroupInfo { get; set; }

    /// <summary>True when a GroupInfo exists but was not served to this caller, so a client can say
    /// "ask to be let in" rather than "this group has no rejoin point".</summary>
    public bool GroupInfoWithheld { get; set; }

    /// <summary>Every generation, oldest first, including terminated ones - their messages are still
    /// in the context and a client needs to know the generation existed to explain a stretch of
    /// history it cannot read, rather than rendering it as corrupt.</summary>
    public List<MlsGenerationDto> Generations { get; set; } = new();
}

/// <summary>One generation, minus its GroupInfo.</summary>
[Facet(typeof(MlsGroupGeneration), nameof(MlsGroupGeneration.MlsGroupInfo))]
public partial class MlsGenerationDto
{
}

public class MlsToggleResultDto
{
    public string ContextId { get; set; } = null!;
    public bool Encrypted { get; set; }

    /// <summary>The generation now active, or the last one that existed when disabling.</summary>
    public int? Generation { get; set; }

    /// <summary>Set on disable: messages sent under this generation stay ciphertext and are readable
    /// only by devices that still hold that group. Nothing is decrypted or rewritten.</summary>
    public int? TerminatedGeneration { get; set; }

    /// <summary>True when the context was already in the requested state and nothing changed.</summary>
    public bool AlreadyInRequestedState { get; set; }

    /// <summary>
    /// Member devices that got no Welcome for the generation just minted, and therefore cannot read
    /// anything sent under it.
    /// </summary>
    public List<UnreachableDeviceDto> UnreachableDevices { get; set; } = new();
}

/// <summary>Returned on 409 so a client that lost the race to commit knows exactly where the group
/// actually is and can catch up from there instead of guessing.</summary>
public class MlsEpochConflictDto
{
    public long CurrentEpoch { get; set; }
    public long RejectedEpoch { get; set; }

    /// <summary>Generation the context is actually on.</summary>
    public int CurrentGeneration { get; set; }

    public int RejectedGeneration { get; set; }

    public string Reason { get; set; } = null!;
}

/// <summary>Returned when a send does not match the context's encryption state, so the client can
/// refresh, re-encrypt (or stop encrypting) and retry rather than guessing why it was refused.</summary>
public class MlsSendConflictDto
{
    public string ContextId { get; set; } = null!;
    public bool Encrypted { get; set; }
    public int? ActiveGeneration { get; set; }
    public string Reason { get; set; } = null!;
}

public class MlsToggleConflictDto
{
    public string ContextId { get; set; } = null!;
    public bool Encrypted { get; set; }
    public string Reason { get; set; } = null!;

    /// <summary>Set when the toggle was refused by the cooldown rather than by current state.</summary>
    public int? RetryAfterSeconds { get; set; }
}
