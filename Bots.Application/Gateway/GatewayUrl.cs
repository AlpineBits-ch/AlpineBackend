using AppEnvironment;

namespace Bots.Application.Gateway;

/// <summary>Derives the public wss:// Gateway URL from the same InstanceUrl every other public-
/// facing link in this codebase is built from (see Steam's PublicBaseUrl for the analogous pattern).</summary>
internal static class GatewayUrl
{
    public static string Build()
    {
        var instanceUrl = Env.GeneralConfiguration.InstanceUrl;
        var isSecure = instanceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var host = isSecure ? instanceUrl["https://".Length..] : instanceUrl["http://".Length..];
        return $"{(isSecure ? "wss://" : "ws://")}{host}/api/discord/v10/gateway";
    }
}
