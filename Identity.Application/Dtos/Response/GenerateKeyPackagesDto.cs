namespace Identity.Application.Dtos.Response;

public class GenerateKeyPackagesDto
{
    /// <summary>How many single-use key packages to generate and upload.</summary>
    public int Count { get; init; }

    /// <summary>True when this device has no unexpired last-resort package. The client should
    /// generate one and upload it with <c>IsLastResort</c> set - without it, a device that drains
    /// its single-use supply between launches is silently left out of every new group.</summary>
    public bool NeedsLastResort { get; init; }
}
