using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;

namespace Identity.Application.Services.DataExport;

/// <summary>What one assembled archive turned out to be. Returned rather than only logged so a test
/// can assert on the manifest without unzipping the bytes twice.</summary>
public sealed record DataExportArchiveResult(byte[] Content, string ManifestJson, int TotalRows);

/// <summary>
/// Turns the fan-out's fragments into the one file the subject downloads (T1-7).
///
/// <para>A static builder over a list, deliberately separate from the handler that uploads it - the
/// same split <c>RetentionSweep</c> has from <c>RetentionSweepService</c>, and for the same reason:
/// the interesting properties here ("the manifest names every producing service", "a failed fragment
/// is recorded rather than dropped") are assertions, not things to be inferred from a bucket.</para>
///
/// <para><b>This class cannot make the archive safe.</b> It writes what each service handed it,
/// verbatim - it has no way to recognize somebody else's email address inside an opaque JSON
/// document, and a filter here that tried would be a false assurance. The no-third-party-personal-
/// data rule is enforced at each participant's projection, where the code knows which column is
/// whose, and is tested there.</para>
/// </summary>
public static class DataExportArchive
{
    /// <summary>Bumped when the archive layout changes in a way a consumer would have to notice.
    /// Written into the manifest so a portability tool reading an old archive can tell.</summary>
    public const int FormatVersion = 1;

    public const string ManifestFileName = "manifest.json";

    /// <summary>The one convention every file in the archive is written with - see
    /// <see cref="UserDataExportJson"/> for why it is on the contract rather than here.</summary>
    private static readonly JsonSerializerOptions ManifestOptions = UserDataExportJson.IndentedOptions;

    public static DataExportArchiveResult Build(
        string exportId,
        string userId,
        IReadOnlyCollection<UserDataExportFragment> fragments,
        DateTimeOffset generatedAt)
    {
        // Stable order regardless of the order the acknowledgements happened to arrive in, so two
        // exports of the same account produce comparable archives.
        var ordered = fragments.OrderBy(f => f.Service, StringComparer.Ordinal).ToList();

        var entries = new List<(string File, byte[] Bytes, UserDataExportFragment Fragment)>();

        foreach (var fragment in ordered)
        {
            var file = $"{fragment.Service}.json";
            var body = Normalize(fragment.FragmentJson);
            entries.Add((file, Encoding.UTF8.GetBytes(body), fragment));
        }

        var manifest = new
        {
            formatVersion = FormatVersion,
            exportId,
            userId,
            generatedAt,
            // Stated in the archive itself, not only in the spec. Somebody reading this file years
            // from now - a subject, a successor engineer, a regulator - should not have to infer the
            // rule from what happens to be absent.
            notice =
                "This archive contains personal data about the account named by userId only. Other "
                + "people appear as opaque identifiers; their email addresses, phone numbers and IP "
                + "addresses are deliberately not included.",
            services = entries.Select(e => new
            {
                service = e.Fragment.Service,
                file = e.File,
                bytes = e.Bytes.Length,
                rowCounts = e.Fragment.RowCounts,
                rows = e.Fragment.RowCounts.Values.Sum(),
                error = e.Fragment.Error,
            }).ToList(),
        };

        var manifestJson = JsonSerializer.Serialize(manifest, ManifestOptions);

        using var buffer = new MemoryStream();

        // Left open so the archive is flushed by Dispose before ToArray() reads the stream - a zip
        // read before its central directory is written is a truncated file, and the symptom is an
        // archive that opens fine in one tool and not in another.
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, ManifestFileName, Encoding.UTF8.GetBytes(manifestJson));

            foreach (var (file, bytes, _) in entries)
                Write(zip, file, bytes);
        }

        return new DataExportArchiveResult(
            buffer.ToArray(),
            manifestJson,
            ordered.Sum(f => f.RowCounts.Values.Sum()));
    }

    private static void Write(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    /// <summary>
    /// Guarantees every file in the archive is parseable JSON.
    ///
    /// <para>A participant that answered with something unparseable - a truncated payload, an empty
    /// string from a handler that returned before serializing - would otherwise put a file in the
    /// archive that no portability tool can read, and Art. 20 asks for a "structured, commonly used,
    /// machine-readable" format. The bad content is preserved as a string rather than dropped: it is
    /// still the only answer that service gave, and discarding it would hide the bug.</para>
    /// </summary>
    private static string Normalize(string? fragmentJson)
    {
        if (string.IsNullOrWhiteSpace(fragmentJson)) return "{}";

        try
        {
            using var _ = JsonDocument.Parse(fragmentJson);
            return fragmentJson;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new
            {
                error = "This service returned a fragment that is not valid JSON. The raw response is preserved below.",
                raw = fragmentJson,
            }, ManifestOptions);
        }
    }
}
