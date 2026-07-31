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

/// <summary>One push transport endpoint of one installation.</summary>
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

    /// <summary>Points an existing row at whoever registered the token this time.</summary>
    public void ReassignTo(string userId, string? deviceId)
    {
        UserId = userId;
        DeviceId = deviceId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
