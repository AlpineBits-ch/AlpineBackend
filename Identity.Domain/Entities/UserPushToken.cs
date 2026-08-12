using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

public class CreateUserPushTokenParams
{
    public string UserId { get; init; } = null!;
    public string Token { get; init; } = null!;
    public PushTokenKind Kind { get; init; }
    public string? DeviceId { get; init; }

    /// <summary>Required for <see cref="PushTokenKind.WebPush"/>, meaningless otherwise.</summary>
    public string? P256dh { get; init; }

    /// <summary>Required for <see cref="PushTokenKind.WebPush"/>, meaningless otherwise.</summary>
    public string? Auth { get; init; }
}

/// <summary>One push transport endpoint of one installation.</summary>
public class UserPushToken : BaseEntity<UserPushToken>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "uptk";

    /// <summary>The routable identity of this endpoint.</summary>
    public string Token { get; set; } = null!;

    public PushTokenKind Kind { get; set; }

    /// <summary>
    /// The subscription's <c>p256dh</c> key: a base64url uncompressed P-256 point (87 chars), the
    /// browser's ECDH public key.
    /// </summary>
    public string? P256dh { get; set; }

    /// <summary>The subscription's <c>auth</c> secret: 16 random bytes, 22 chars base64url, mixed into
    /// the RFC 8291 key derivation. Null for every kind but
    /// <see cref="PushTokenKind.WebPush"/>.</summary>
    public string? Auth { get; set; }

    /// <summary>The RFC 8030 push endpoint for a Web Push row, and null for anything else - so a
    /// caller cannot accidentally treat an FCM token as a URL to POST to.</summary>
    [NotMapped]
    public string? Endpoint => Kind == PushTokenKind.WebPush ? Token : null;

    /// <summary>Whether this row carries everything its transport needs to be sent to.</summary>
    [NotMapped]
    public bool IsComplete => Kind != PushTokenKind.WebPush
                              || (!string.IsNullOrWhiteSpace(P256dh) && !string.IsNullOrWhiteSpace(Auth));

    public string UserId { get; set; } = null!;
    public virtual ApplicationUser User { get; set; } = null!;

    /// <summary>FK to <see cref="UserDevice.Id"/> (the row id, not the client-supplied
    /// ClientDeviceId). Null for tokens registered without a device.</summary>
    public string? DeviceId { get; set; }
    public virtual UserDevice? Device { get; set; }

    public static UserPushToken Create(CreateUserPushTokenParams createParams)
    {
        var date = DateTimeOffset.UtcNow;
        return new UserPushToken
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            UserId = createParams.UserId,
            Token = createParams.Token,
            Kind = createParams.Kind,
            DeviceId = createParams.DeviceId,
            P256dh = createParams.P256dh,
            Auth = createParams.Auth,
        };
    }

    /// <summary>Points an existing row at whoever registered the token this time.</summary>
    public void ReassignTo(string userId, string? deviceId)
    {
        UserId = userId;
        DeviceId = deviceId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Re-points an existing row and refreshes its Web Push keys.</summary>
    public void ReassignTo(string userId, string? deviceId, string? p256dh, string? auth)
    {
        if (p256dh is not null) P256dh = p256dh;
        if (auth is not null) Auth = auth;
        ReassignTo(userId, deviceId);
    }
}
