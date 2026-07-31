using System.Net;
using Echo.E2E.Tests.Hosts;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Assertions that report a spawned service's captured output, built only when they fail.
/// </summary>
internal static class E2EAssert
{
    /// <summary>Asserts a 2xx, reporting the body and the service's log if it is not.</summary>
    public static async Task SucceededAsync(
        HttpResponseMessage response, SpawnedServiceProcess service, string what)
    {
        if (response.IsSuccessStatusCode) return;
        Assert.Fail(await DiagnosticsAsync(
            response, service, $"{what} (got {(int)response.StatusCode} {response.StatusCode})"));
    }

    /// <summary>Asserts an exact status.</summary>
    public static async Task HasStatusAsync(
        HttpResponseMessage response, HttpStatusCode expected, SpawnedServiceProcess service, string what)
    {
        if (response.StatusCode == expected) return;
        Assert.Fail(await DiagnosticsAsync(
            response, service, $"{what} (expected {expected}, got {(int)response.StatusCode} {response.StatusCode})"));
    }

    /// <summary>
    /// Asserts a condition that is not itself an HTTP result - typically the outcome of polling a
    /// service's database until a cross-service message has been handled.
    /// </summary>
    public static void Held(bool condition, SpawnedServiceProcess service, string what)
    {
        if (condition) return;
        Assert.Fail($"{what}\n{service.CapturedOutput}");
    }

    private static async Task<string> DiagnosticsAsync(
        HttpResponseMessage response, SpawnedServiceProcess service, string what) =>
        $"{what}: {await response.Content.ReadAsStringAsync()}\n{service.CapturedOutput}";
}
