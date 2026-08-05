namespace Identity.Contracts.Bus.Response;

public class IsUserAdministrativeResponse
{
    /// <summary>True only for <c>UserType.Admin</c>.</summary>
    public bool IsAdministrative { get; set; }

    /// <summary>The caller's staff tier: <c>None</c>, <c>Moderator</c> or <c>Admin</c>.</summary>
    public string Role { get; set; } = "None";

    /// <summary>Convenience for the common check. True for both tiers.</summary>
    public bool IsStaff { get; set; }

    public string? UserName { get; set; }
}