using Facet;
using Messaging.Domain.Aggregates;

namespace Messaging.Application.Dtos.Response;

[Facet(typeof(Conversation), NestedFacets = [typeof(ConversationMemberDto)])]
public partial class ConversationDto
{
    /// <summary>
    /// Member devices that received no Welcome and therefore cannot read this conversation.
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

/// <summary>Body of the refusal when creation is rejected for incomplete device coverage.</summary>
public class CreateConversationRejectedDto
{
    public string Reason { get; set; } = null!;
    public List<UnreachableDeviceDto> UnreachableDevices { get; set; } = new();
}