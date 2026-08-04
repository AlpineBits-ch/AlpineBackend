using System.Text.Json;

namespace Identity.Contracts.Bus.Response;

/// <summary>The serialization convention every export fragment is written with (T1-7).</summary>
public static class UserDataExportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Same convention, indented - for the manifest, which is the one file in the archive a
    /// human is expected to open first.</summary>
    public static readonly JsonSerializerOptions IndentedOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
