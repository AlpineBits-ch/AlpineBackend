using Facet;
using Messaging.Domain.Aggregates;

namespace Messaging.Application.Dtos.Response;

[Facet(typeof(Conversation), NestedFacets = [typeof(ConversationMemberDto)])]
public partial class ConversationDto
{
    /// <summary>
    /// Member devices that received no Welcome and therefore cannot read this conversation.
    ///
    /// <para>Only ever populated on creation. Added to the existing shape rather than wrapped in a
    /// new envelope so clients already reading a ConversationDto off this response keep working -
    /// and so a client that ignores the field is exactly as broken as it was before, not more.</para>
    /// </summary>
    public List<UnreachableDeviceDto> UnreachableDevices { get; set; } = new();
}

/// <summary>A device that could not be added to an encrypted conversation's group, named so the
/// caller can say which one rather than reporting a generic failure.</summary>
public class UnreachableDeviceDto
{
    public string UserId { get; set; } = null!;

    /// <summary>Client device id.</summary>
    public string DeviceId { get; set; } = null!;

    public string DeviceName { get; set; } = null!;
}

/// <summary>Body of the refusal when creation is rejected for incomplete device coverage. Carries
/// the same device list as the success path, so a client can present one thing either way.</summary>
public class CreateConversationRejectedDto
{
    public string Reason { get; set; } = null!;
    public List<UnreachableDeviceDto> UnreachableDevices { get; set; } = new();
}