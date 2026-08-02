using Messaging.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Messaging.Tests.Helpers;

/// <summary>Collaborators that every MLS test needs and no MLS test is about.</summary>
public static class TestMlsServices
{
    /// <summary>
    /// A real <see cref="MlsDeviceCoverageService"/> over the given bus.
    ///
    /// <para>Not a stub: it swallows a bus that cannot answer and reports nothing, which is exactly
    /// what the tests that do not care about coverage want, while the tests that do care get the
    /// production behaviour by configuring the bus.</para>
    /// </summary>
    public static MlsDeviceCoverageService Coverage(IMessageBus bus) =>
        new(bus, NullLogger<MlsDeviceCoverageService>.Instance);
}
