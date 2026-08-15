namespace Guild.Application.Bus.Events.Voice;

/// <summary>Scheduled when a ring is created and delivered once its deadline has passed.</summary>
public class VoiceRingTimeoutCheck
{
    public required string RingId { get; set; }
}
