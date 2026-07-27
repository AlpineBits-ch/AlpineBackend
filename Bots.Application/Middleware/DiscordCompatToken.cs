using System.Text;

namespace Bots.Application.Middleware;

/// <summary>Packs/unpacks the Discord-compat bot token: base64(client_id:client_secret).</summary>
public static class DiscordCompatToken
{
    public static string Pack(string clientId, string clientSecret) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

    public static bool TryUnpack(string packed, out string clientId, out string clientSecret)
    {
        clientId = "";
        clientSecret = "";

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(packed));
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex < 0) return false;

        clientId = decoded[..separatorIndex];
        clientSecret = decoded[(separatorIndex + 1)..];
        return true;
    }
}
