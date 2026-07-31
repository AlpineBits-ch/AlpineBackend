using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

public class CreateLoginSessionParams
{
    public string UserId { get; init; } = null!;
    public string DeviceName { get; init; } = null!;
    public DeviceType DeviceType { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? DeviceId { get; init; }
}

/// <summary>
/// A single login (one grant-type exchange at /connect/token, refreshed thereafter). Tracked
/// separately from OpenIddict's own token table so a user can see and revoke individual logins by
/// device/IP - OpenIddict's default schema has no room for that metadata. See
/// ConnectController.Exchange: every non-refresh grant creates one of these and stamps its Id onto
/// the issued principal as a "session_id" claim; the refresh-token branch reads that claim back and
/// rejects the refresh if RevokedAt is set.
/// </summary>
public class LoginSession : BaseEntity<LoginSession>, IPrefixedEntity
{
    public static string Prefix { get; } = "lgsn";

    public string UserId { get; set; } = null!;
    public string DeviceName { get; set; } = null!;
    public DeviceType DeviceType { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The registered <see cref="UserDevice"/> this login came from, when the client sent its
    /// device id at /connect/token. A device has many sequential sessions (every re-login makes a
    /// new one), which is why this is a link rather than the two being one row. Null for clients
    /// that don't send a device id yet, and for logins that happened before this column existed -
    /// those sessions simply can't take their push tokens down with them on revoke.
    /// </summary>
    public string? DeviceId { get; set; }
    public UserDevice? Device { get; set; }

    public ApplicationUser User { get; set; } = null!;

    public static LoginSession Create(CreateLoginSessionParams createParams)
    {
        var date = DateTimeOffset.UtcNow;
        return new LoginSession
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = createParams.UserId,
            DeviceName = createParams.DeviceName,
            DeviceType = createParams.DeviceType,
            IpAddress = createParams.IpAddress,
            UserAgent = createParams.UserAgent,
            DeviceId = createParams.DeviceId,
            LastUsedAt = date,
        };
    }

    public bool IsRevoked => RevokedAt is not null;

    public void Revoke()
    {
        RevokedAt = DateTimeOffset.UtcNow;
    }

    public void Touch()
    {
        LastUsedAt = DateTimeOffset.UtcNow;
    }
}
