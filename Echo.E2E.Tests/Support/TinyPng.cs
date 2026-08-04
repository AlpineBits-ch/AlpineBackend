using System.IO.Compression;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Builds a real PNG in memory, by hand.
///
/// <para>The link-preview pipeline decodes whatever an origin serves as its <c>og:image</c>,
/// measures it, resizes it, computes a blur placeholder and stores it - so a preview test needs
/// bytes that are genuinely a decodable image of a known size. A hardcoded 1x1 would run the same
/// code but assert nothing useful about dimensions, and pulling ImageSharp into this project to
/// generate one would drag in the Six Labors licence file that only the services carry.</para>
///
/// <para>So: a minimal encoder. Truecolour, 8-bit, no interlacing, one zlib stream over unfiltered
/// scanlines - the simplest thing a PNG decoder is required to accept.</para>
/// </summary>
internal static class TinyPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>A <paramref name="width"/> x <paramref name="height"/> RGB gradient.</summary>
    public static byte[] Create(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write(Signature);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type 2 = truecolour RGB
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(png, "IHDR", header);

        // Each scanline is prefixed with its filter type; 0 means "none", which keeps this encoder
        // to arithmetic a reader can check by eye.
        var raw = new byte[height * (1 + width * 3)];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[offset++] = (byte)(x * 255 / Math.Max(1, width - 1));
                raw[offset++] = (byte)(y * 255 / Math.Max(1, height - 1));
                raw[offset++] = 128;
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);

        return png.ToArray();
    }

    private static void WriteChunk(Stream target, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        target.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        // The CRC covers the type and the data, but not the length.
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32([.. typeBytes, .. data]));
        target.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
