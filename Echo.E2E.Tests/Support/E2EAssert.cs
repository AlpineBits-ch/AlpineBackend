using System.Net;
using Echo.E2E.Tests.Hosts;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Assertions that report a spawned service's captured output, built only when they fail.
///
/// <para>The diagnostics here are expensive: a response body plus the entire captured
/// stdout/stderr of the service that answered. Interpolated straight into <c>Assert.That</c>'s
/// message parameter, as they were at every call site, all of that is built on <em>every</em>
/// call - arguments are evaluated before the assertion is even considered - so a fully passing
/// run read and copied a service's whole log at each of ~50 checkpoints and discarded the result.
/// The HTTP ones also read the response body on the success path, which each caller then read
/// again for the payload it actually wanted.</para>
///
/// <para>That was not only waste. Reading a service's captured output races the child process
/// still writing it, and doing it on the success path is what turned a latent race into a flake
/// that surfaced in an unrelated test - see <see cref="ProcessOutputBuffer"/>, which fixed the
/// race itself. Not doing the work at all removes the remaining reason to touch that buffer on a
/// passing run.</para>
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

    /// <summary>
    /// Asserts an exact status. Used where the specific code is the point - a 403 that proves a
    /// permission gate fired, a 202 that proves work was queued rather than done inline.
    /// </summary>
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
