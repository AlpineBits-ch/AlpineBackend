namespace Identity.Contracts.Bus.Response;

/// <summary>One account as the moderation console lists it.</summary>
public class AdminUserSummary
{
    public string Id { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string Status { get; set; } = string.Empty;
    public string UserType { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class SearchUsersResponse
{
    public List<AdminUserSummary> Users { get; set; } = [];

    /// <summary>Total matches, not the page size.</summary>
    public int Total { get; set; }
}

public class GetAdminUserDetailResponse
{
    public AdminUserSummary? User { get; set; }

    public string? Bio { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public bool LockedOut { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public int DeviceCount { get; set; }

    /// <summary>Set while a deletion is pending or in flight, so the console does not offer a ban on
    /// an account that is halfway through being erased.</summary>
    public DateTimeOffset? DeletionRequestedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}

public class GetUserNamesResponse
{
    /// <summary>Id to display name.</summary>
    public Dictionary<string, string> Names { get; set; } = [];
}

public class GetPlatformStatsResponse
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int BannedUsers { get; set; }
    public int PendingDeletion { get; set; }
    public int DeletedUsers { get; set; }
    public int BotAccounts { get; set; }
    public int StaffAccounts { get; set; }
    public int UnverifiedEmail { get; set; }
    public int NewUsers24h { get; set; }
    public int NewUsers7d { get; set; }
    public int NewUsers30d { get; set; }
}

public class SetUserRoleResponse
{
    public bool Success { get; set; }

    /// <summary><c>not_found</c>, <c>self_action</c>, <c>invalid_role</c>, <c>bot_account</c>,
    /// <c>last_administrator</c>, <c>account_inactive</c>, or <c>no_change</c>. Null on success.</summary>
    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>The account's tier after the call, whether or not anything changed.</summary>
    public string Role { get; set; } = string.Empty;

    public string? UserName { get; set; }
}

public class SetUserModerationStatusResponse
{
    public bool Success { get; set; }

    /// <summary>Machine-readable refusal: <c>not_found</c>, <c>self_action</c>,
    /// <c>protected_account</c>, <c>invalid_state</c>, or <c>no_change</c>. Null on success.</summary>
    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>The account's status after the call, whether or not anything changed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Where a status email can be sent.</summary>
    public string? Email { get; set; }

    public string? UserName { get; set; }
}
