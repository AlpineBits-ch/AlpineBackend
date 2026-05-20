namespace Federation.Application.Dtos.Events;

public abstract class FederationEvent
{
    
}


// mock event for now
public class MessageReceived : FederationEvent
{
    public string Message { get; set; }   
}