using System.Security.Cryptography;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Minimal RFC 6238 TOTP code generator, algorithm-compatible with ASP.NET Core Identity's
/// <c>AuthenticatorTokenProvider&lt;TUser&gt;</c> (HMAC-SHA1, 30s time step, 6 digits, no modifier
/// - the same parameters MfaEndpoint.BuildOtpAuthUri advertises: algorithm=SHA1, digits=6,
/// period=30).
/// </summary>
public static class Totp
{
    public static string GenerateCode(string base32Secret, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Secret);
        var timestepNumber = (ulong)((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30);

        // Big-endian 8-byte counter, per RFC 4226/6238.
        var timestepBytes = BitConverter.GetBytes(timestepNumber);
        if (BitConverter.IsLittleEndian) Array.Reverse(timestepBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(timestepBytes);

        var offset = hash[^1] & 0xf;
        var binaryCode = (hash[offset] & 0x7f) << 24
                          | (hash[offset + 1] & 0xff) << 16
                          | (hash[offset + 2] & 0xff) << 8
                          | (hash[offset + 3] & 0xff);

        return (binaryCode % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();

        var output = new List<byte>();
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            var value = alphabet.IndexOf(c);
            if (value < 0) continue; // ignore stray formatting characters, if any

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }

        return output.ToArray();
    }
}
