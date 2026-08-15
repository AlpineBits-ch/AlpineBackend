using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts;

namespace Guild.Application.Bus.Events.Voice;

/// <summary>Lets a ring nobody answered lapse.</summary>
public class VoiceRingTimeoutCheckHandler
{
    public static Task Handle(VoiceRingTimeoutCheck message, VoiceRingService rings) =>
        rings.ResolveAsync(message.RingId, VoiceRingStatus.Expired, VoiceRingReason.TimedOut, null);
}
