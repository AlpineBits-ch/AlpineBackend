namespace Identity.Contracts.Bus.Request;

/// <summary>User lookup for the moderation console.</summary>
public class SearchUsersRequest
{
    /// <summary>Free text.</summary>
    public string? Query { get; set; }

    /// <summary>Name of an <c>Identity.Domain.Enums.UserStatus</c> value, or null for any. A string
    /// rather than the enum because Identity.Contracts is referenced by services that have no
    /// business knowing Identity's internal enums.</summary>
    public string? Status { get; set; }

    /// <summary>Name of an <c>Identity.Domain.Enums.UserType</c> value, or null for any.</summary>
    public string? UserType { get; set; }

    public int Limit { get; set; } = 25;
    public int Offset { get; set; }
}

/// <summary>One account, in the detail a moderator needs to decide something.</summary>
public class GetAdminUserDetailRequest
{
    public string UserId { get; set; } = string.Empty;
}

/// <summary>Instance-level counts for the console dashboard.</summary>
public class GetPlatformStatsRequest;

/// <summary>Sets an account's sign-in status on behalf of the moderation console.</summary>
public class SetUserModerationStatusRequest
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>The staff member acting.</summary>
    public string ActorUserId { get; set; } = string.Empty;

    /// <summary>True to ban, false to restore to Active.</summary>
    public bool Banned { get; set; }

    /// <summary>Free text for Identity's audit row.</summary>
    public string? Reason { get; set; }
}

/// <summary>Promotes or demotes a staff account.</summary>
public class SetUserRoleRequest
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>The administrator making the change. Recorded, and used for the self-action check.</summary>
    public string ActorUserId { get; set; } = string.Empty;

    /// <summary>Target tier: <c>Default</c>, <c>Moderator</c> or <c>Admin</c>.</summary>
    public string Role { get; set; } = "Default";
}
