using System.ComponentModel.DataAnnotations.Schema;
using Identity.Domain.Aggregates;
using Persistence;

namespace Identity.Domain.Entities;


public class CreateUserKeyPackageParams
{
    public string UserId { get; init; }
    public string DeviceId { get; init; }
    public byte[] KeyPackage { get; init; }
}

public class UserKeyPackage : BaseEntity<UserKeyPackage>, IPrefixedEntity
{
    public string UserId { get; set; } = null!;
    public string DeviceId { get; set; } = null!;
    public byte[] KeyPackage { get; set; } = null!;  // serialized public MLS KeyPackage
    public int CipherSuite { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public UserDevice Device { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
    public static string Prefix { get; } = "ukpk";

    /// <summary>
    /// This derrives the cipher suite from the first two bytes of the key package
    /// This is coming as 16 bit big endian. The first byte MUST be 0x00 and the second byte anywhere from 0x01 to 0x03
    /// Anything else is invalid
    /// </summary>
    /// <param name="cipher"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
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
        };
    }
}