using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Aggregates;
using Persistence;

namespace Identity.Domain.Entities;


public class CreateUserKeyPackageParams
{
    public string UserId { get; init; }
    public string DeviceId { get; init; }
    public byte[] KeyPackage { get; init; }

    /// <summary>When the KeyPackage's own MLS lifetime runs out.</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>See <see cref="UserKeyPackage.IsLastResort"/>.</summary>
    public bool IsLastResort { get; init; }
}

public class UserKeyPackage : BaseEntity<UserKeyPackage>, IPrefixedEntity
{
    /// <summary>Fallback lifetime for a package whose uploader did not report one.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    public string UserId { get; set; } = null!;
    public string DeviceId { get; set; } = null!;
    public byte[] KeyPackage { get; set; } = null!;  // serialized public MLS KeyPackage
    public int CipherSuite { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// A reusable package of last resort, handed out only when a device has no single-use package
    /// left.
    /// </summary>
    public bool IsLastResort { get; set; }

    public UserDevice Device { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
    public static string Prefix { get; } = "ukpk";

    public bool IsUsableAt(DateTime now) =>
        ExpiresAt > now && (IsLastResort || ConsumedAt is null);

    /// <summary>
    /// This derrives the cipher suite from the first two bytes of the key package This is coming as
    /// 16 bit big endian.
    /// </summary>
    public static int GetCipherSuite(byte[] cipher)
    {
        if (cipher.Length < 4) throw new ArgumentException("Invalid key package length");
    
        if (cipher[2] != 0x00) throw new ArgumentException("Invalid cipher suite");
    
        return cipher[3] switch
        {
            0x01 => 1,
            0x02 => 2,
            0x03 => 3,
            _ => throw new ArgumentException($"Unsupported cipher suite: 0x{cipher[3]:X2}")
        };
    }
    
    public static UserKeyPackage Create(CreateUserKeyPackageParams dto)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;
        return new UserKeyPackage
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            UserId = dto.UserId,
            DeviceId = dto.DeviceId,
            KeyPackage = dto.KeyPackage,
            CipherSuite = GetCipherSuite(dto.KeyPackage),
            ExpiresAt = dto.ExpiresAt ?? date.Add(DefaultLifetime),
            IsLastResort = dto.IsLastResort,
        };
    }
}