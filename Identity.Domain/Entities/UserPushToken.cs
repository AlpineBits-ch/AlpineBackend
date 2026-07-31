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
}

/// <summary>
/// One push transport endpoint of one installation. Replaces the old UserDeviceToken (FCM) and
/// UserVoipToken (APNs VoIP) pair, which were the same two columns twice over.
///
/// <para><see cref="DeviceId"/> is what makes this a *device* record rather than a bag of strings
/// hanging off the account: it lets a send skip the device that is already handling the interaction
/// in-app, and lets device removal / session revocation take the matching tokens down with them.
/// It stays nullable because a client can register a push token before it has registered its MLS
/// device (and older builds never send one at all) - an unattributed token still delivers, it just
/// can't be targeted or cleaned up by device.</para>
/// </summary>
public class UserPushToken : BaseEntity<UserPushToken>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "uptk";

    public string Token { get; set; } = null!;
    public PushTokenKind Kind { get; set; }

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
        };
    }

    /// <summary>
    /// Points an existing row at whoever registered the token this time. FCM and APNs both recycle
    /// tokens across accounts on the same handset (reinstall, account switch), so the same string
    /// legitimately arrives for a different user later - reassigning is the only way the previous
    /// owner stops receiving that handset's notifications.
    /// </summary>
    public void ReassignTo(string userId, string? deviceId)
    {
        UserId = userId;
        DeviceId = deviceId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
