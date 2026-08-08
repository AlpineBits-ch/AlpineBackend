using Echo.Realtime.Devices;
using Messaging.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Messaging.Tests.Helpers;

/// <summary>Collaborators that every MLS test needs and no MLS test is about.</summary>
public static class TestMlsServices
{
    /// <summary>
    /// A request carrying the caller's device id, which is what every client in the field stamps on
    /// every call. The server needs it to tell the device that built the group - and therefore has no
    /// Welcome addressed to it - apart from one that was left out of the group.
    /// </summary>
    /// <param name="deviceId">Null for a client that sends no header, which is the case worth pinning
    /// separately: it must degrade to a narrower report rather than to a wrong one.</param>
    public static HttpContext HttpWithDevice(string? deviceId)
    {
        var http = new DefaultHttpContext();
        if (deviceId is not null) http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;
        return http;
    }

    /// <summary>A real <see cref="MlsDeviceCoverageService"/> over the given bus.</summary>
    public static MlsDeviceCoverageService Coverage(IMessageBus bus) =>
        new(bus, NullLogger<MlsDeviceCoverageService>.Instance);
}
