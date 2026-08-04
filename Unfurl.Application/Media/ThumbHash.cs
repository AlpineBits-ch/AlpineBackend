using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Unfurl.Application.Media;

/// <summary>
/// Produces the compact blurred placeholder carried in
/// <c>EmbedMediaPayload.Placeholder</c> - the thing a client renders in the card's image slot while
/// the real image is still downloading, so a preview does not pop in as a grey rectangle.
///
/// <para><b>The encoding is BlurHash, not Discord's thumbhash</b>, and
/// <c>placeholder_version: 1</c> is what says so. Discord's format has no published specification,
/// whereas BlurHash is specified, tiny (about 30 characters), and has maintained decoders for web,
/// iOS, Android and Flutter - so our clients get one dependency instead of a reverse-engineering
/// project. The version field exists precisely so this can change later without breaking clients
/// holding old messages.</para>
///
/// <para>The image is downscaled to a small working size first. The transform below is O(pixels ×
/// components), and running it over a 1280px source would cost tens of milliseconds per preview for
/// a result that is, by construction, a blur.</para>
/// </summary>
public static class ThumbHash
{
    private const string Base83 =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

    private const int WorkingSize = 32;
    private const int ComponentsX = 4;
    private const int ComponentsY = 3;

    public static string Encode(Image<Rgba32> source)
    {
        using var small = source.Clone(x => x.Resize(new ResizeOptions
        {
            Size = new Size(WorkingSize, WorkingSize),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Triangle,
        }));

        var width = small.Width;
        var height = small.Height;

        // Linear-light RGB. Averaging in sRGB - which is what skipping this would do - makes every
        // blur noticeably darker than the image it came from, because sRGB is perceptual, not
        // linear in intensity.
        var linear = new double[height, width, 3];
        small.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    linear[y, x, 0] = SrgbToLinear(row[x].R);
                    linear[y, x, 1] = SrgbToLinear(row[x].G);
                    linear[y, x, 2] = SrgbToLinear(row[x].B);
                }
            }
        });

        var factors = new double[ComponentsX * ComponentsY][];

        for (var j = 0; j < ComponentsY; j++)
        for (var i = 0; i < ComponentsX; i++)
        {
            var normalisation = i == 0 && j == 0 ? 1.0 : 2.0;
            var factor = new double[3];

            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var basis = normalisation
                            * Math.Cos(Math.PI * i * x / width)
                            * Math.Cos(Math.PI * j * y / height);

                factor[0] += basis * linear[y, x, 0];
                factor[1] += basis * linear[y, x, 1];
                factor[2] += basis * linear[y, x, 2];
            }

            var scale = 1.0 / (width * height);
            factor[0] *= scale;
            factor[1] *= scale;
            factor[2] *= scale;

            factors[j * ComponentsX + i] = factor;
        }

        var dc = factors[0];
        var ac = factors.Skip(1).ToArray();

        var hash = new System.Text.StringBuilder();

        var sizeFlag = ComponentsX - 1 + (ComponentsY - 1) * 9;
        hash.Append(Encode83(sizeFlag, 1));

        double maximumValue;
        if (ac.Length > 0)
        {
            var actualMax = ac.SelectMany(f => f).Max(Math.Abs);
            var quantisedMax = Math.Max(0, Math.Min(82, (int)Math.Floor(actualMax * 166 - 0.5)));
            maximumValue = (quantisedMax + 1) / 166.0;
            hash.Append(Encode83(quantisedMax, 1));
        }
        else
        {
            maximumValue = 1;
            hash.Append(Encode83(0, 1));
        }

        hash.Append(Encode83(EncodeDc(dc), 4));
        foreach (var factor in ac) hash.Append(Encode83(EncodeAc(factor, maximumValue), 2));

        return hash.ToString();
    }

    private static int EncodeDc(double[] value) =>
        (LinearToSrgb(value[0]) << 16) + (LinearToSrgb(value[1]) << 8) + LinearToSrgb(value[2]);

    private static int EncodeAc(double[] value, double maximumValue)
    {
        var quant = value
            .Select(v => Math.Max(0, Math.Min(18,
                (int)Math.Floor(SignPow(v / maximumValue, 0.5) * 9 + 9.5))))
            .ToArray();

        return quant[0] * 19 * 19 + quant[1] * 19 + quant[2];
    }

    private static string Encode83(int value, int length)
    {
        var result = new char[length];
        for (var i = 0; i < length; i++)
        {
            var digit = value / (int)Math.Pow(83, length - i - 1) % 83;
            result[i] = Base83[digit];
        }
        return new string(result);
    }

    private static double SrgbToLinear(byte component)
    {
        var v = component / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static int LinearToSrgb(double value)
    {
        var v = Math.Max(0, Math.Min(1, value));
        return v <= 0.0031308
            ? (int)(v * 12.92 * 255 + 0.5)
            : (int)((1.055 * Math.Pow(v, 1 / 2.4) - 0.055) * 255 + 0.5);
    }

    private static double SignPow(double value, double exponent) =>
        Math.Sign(value) * Math.Pow(Math.Abs(value), exponent);
}
