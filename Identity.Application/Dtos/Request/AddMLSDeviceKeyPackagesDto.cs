namespace Identity.Application.Dtos.Request;

public class AddMLSDeviceKeyPackagesDto
{
    public ICollection<PackageDto> KeyPackages { get; set; }
}

public class PackageDto
{
    public byte[] KeyPackage { get; set; }
}
