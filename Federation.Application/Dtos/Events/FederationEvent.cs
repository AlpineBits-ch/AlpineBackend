using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Federation.Application.Dtos.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$eventType")]
[JsonDerivedType(typeof(MessageReceived), "messageReceived")]
// Add all derived types here on the BASE class
public  class FederationEvent
{
}

public class MessageReceived : FederationEvent
{
    public string Message { get; set; } = null!;
}

[JsonSerializable(typeof(FederationEvent))]
[JsonSerializable(typeof(MessageReceived))]           // Register concrete types too
[JsonSerializable(typeof(List<FederationEvent>))]
public partial class EventJsonContext : JsonSerializerContext
{
}