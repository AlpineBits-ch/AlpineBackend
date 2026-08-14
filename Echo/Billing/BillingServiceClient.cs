using System.Text;
using AppEnvironment;

namespace Echo.Billing;

/// <summary>What the Billing service answered, kept verbatim.</summary>
public sealed record BillingReply(int Status, string? Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>The console's channel to the Billing service.</summary>
public sealed class BillingServiceClient(HttpClient http, ILogger<BillingServiceClient> logger)
{
    /// <summary>Whether there is a Billing service to talk to at all.</summary>
    public static bool IsDeployed => Env.License.IsHosted && Env.License.IsBillingConfigured;

    /// <summary>Where the service answers.</summary>
    public static string BaseAddress =>
        Env.License.BillingServiceUrl is { Length: > 0 } configured
            ? configured
            : "http://billing.default.svc.cluster.local";

    public Task<BillingReply> GetAsync(HttpContext caller, string path, CancellationToken ct) =>
        SendAsync(caller, HttpMethod.Get, path, body: null, ct);

    public async Task<BillingReply> SendAsync(
        HttpContext caller, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!IsDeployed)
        {
            return new BillingReply(StatusCodes.Status503ServiceUnavailable,
                """
                {"code":"billing_not_deployed","message":"This instance has no billing service. Entitlements resolve from the license mode and the configured plan table, and there is nothing here to grant or to edit."}
                """);
        }

        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (caller.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization.ToArray());
        }

        try
        {
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            return new BillingReply((int)response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception,
                "The billing service did not answer {Method} {Path} at {Address}.",
                method, path, BaseAddress);

            return new BillingReply(StatusCodes.Status503ServiceUnavailable,
                """
                {"code":"billing_unreachable","message":"The billing service did not answer. Your session is still valid - try again in a moment."}
                """);
        }
    }
}
