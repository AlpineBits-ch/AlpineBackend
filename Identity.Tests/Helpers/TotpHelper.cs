using System.Security.Cryptography;

namespace Identity.Tests.Helpers;

/// <summary>
/// Computes RFC 6238 TOTP codes from a raw Base32 authenticator secret so tests can produce a
/// code that ASP.NET Core Identity's AuthenticatorTokenProvider will actually accept.
///
/// Note: UserManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider)
/// does NOT work for this - the authenticator provider's GenerateAsync intentionally returns an
/// empty string, because in production the code is generated client-side by the user's
/// authenticator app from the secret, never by the server. Tests have to compute it themselves,
/// exactly like a real authenticator app would.
/// </summary>
public static class TotpHelper
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string GenerateCode(string base32Secret, DateTime? at = null)
    {
        var keyBytes = Base32Decode(base32Secret);
        var unixTimestamp = (long)((at ?? DateTime.UtcNow) - UnixEpoch).TotalSeconds;
        var timestepNumber = unixTimestamp / 30;

        var timestepBytes = BitConverter.GetBytes(timestepNumber);
        if (BitConverter.IsLittleEndian) Array.Reverse(timestepBytes);

        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(timestepBytes);

        var offset = hash[^1] & 0xf;
        var binaryCode = ((hash[offset] & 0x7f) << 24)
                          | ((hash[offset + 1] & 0xff) << 16)
                          | ((hash[offset + 2] & 0xff) << 8)
                          | (hash[offset + 3] & 0xff);

        var code = binaryCode % 1000000;
        return code.ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();

        var bitBuffer = 0;
        var bitCount = 0;
        var output = new List<byte>();

        foreach (var c in input)
        {
            var value = alphabet.IndexOf(c);
            if (value < 0) continue;

            bitBuffer = (bitBuffer << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
                output.Add((byte)((bitBuffer >> (bitCount - 8)) & 0xff));
                bitCount -= 8;
            }
        }

        return output.ToArray();
    }
}
