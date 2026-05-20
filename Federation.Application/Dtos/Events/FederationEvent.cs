using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Federation.Application.Dtos.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$eventType")]
public abstract class FederationEvent
{
    
}

[JsonDerivedType(typeof(MessageReceived), "messageReceived")]
public class MessageReceived : FederationEvent
{
    public string Message { get; set; }   
}

[JsonSerializable(typeof(FederationEvent))]


[JsonSerializable(typeof(List<FederationEvent>))]

public partial class EventJsonContext : JsonSerializerContext
{
   
}