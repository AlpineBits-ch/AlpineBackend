using System.Text.Json;

namespace Bots.Tests.Helpers;

/// <summary>
/// Loads credentials for the opt-in live Gateway E2E test. Environment variables
/// (BOTS_E2E_CLIENT_ID/BOTS_E2E_CLIENT_SECRET/BOTS_E2E_BASE_URL) always win; otherwise falls back
/// to Bots.Tests/.e2e-credentials.local.json, a git-ignored file (see .gitignore -
/// *.e2e-credentials.local.json) so a real bot secret is never committed to source control.
/// Populate that file locally, or export the env vars, to run GatewayLiveE2ETests.
/// </summary>
internal static class LocalE2ECredentials
{
    public static (string? ClientId, string? ClientSecret, string BaseUrl) Load()
    {
        var clientId = Environment.GetEnvironmentVariable("BOTS_E2E_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("BOTS_E2E_CLIENT_SECRET");
        var baseUrl = Environment.GetEnvironmentVariable("BOTS_E2E_BASE_URL");

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            var file = FindCredentialsFile();
            if (file is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    clientId ??= root.TryGetProperty("clientId", out var id) ? id.GetString() : null;
                    clientSecret ??= root.TryGetProperty("clientSecret", out var secret) ? secret.GetString() : null;
                    baseUrl ??= root.TryGetProperty("baseUrl", out var url) ? url.GetString() : null;
                }
                catch
                {
                    // malformed local credentials file - fall through with whatever env vars gave us
                }
            }
        }

        return (clientId, clientSecret, baseUrl ?? "https://api.venta.gg");
    }

    /// <summary>Walks up from the test assembly's output directory to find the repo root
    /// (marked by Echo.sln), since the file lives in source, not the build output.</summary>
    private static string? FindCredentialsFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Echo.sln")))
            {
                var credentialsPath = Path.Combine(dir.FullName, "Bots.Tests", ".e2e-credentials.local.json");
                return File.Exists(credentialsPath) ? credentialsPath : null;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
