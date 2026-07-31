namespace Identity.Application.Dtos.Request;

public class AddMLSDeviceKeyPackagesDto
{
    public ICollection<PackageDto> KeyPackages { get; set; } = new List<PackageDto>();
}

public class AddKeyPackagesResultDto
{
    public int Added { get; set; }
}

public class PackageDto
{
    public byte[] KeyPackage { get; set; } = null!;

    /// <summary>When this package's own MLS lifetime runs out.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Marks this as the device's reusable package of last resort.</summary>
    public bool IsLastResort { get; set; }
}
