using System.Text.Json;

namespace Identity.Contracts.Bus.Response;

/// <summary>
/// The serialization convention every export fragment is written with (T1-7).
///
/// <para>Lives on the contract rather than in each service because the archive is read as one
/// document: eight services each choosing their own casing produces a zip where <c>userName</c> and
/// <c>UserName</c> both appear depending on which file you open, and a portability tool consuming it
/// has to special-case every section. Anonymous-type projections make that especially easy to get
/// wrong - a shorthand member (<c>x.UserName</c>) infers a PascalCase name while an explicit one
/// (<c>userName = x.UserName</c>) does not, so the same object can be mixed-case without anyone
/// writing anything that looks inconsistent.</para>
/// </summary>
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
