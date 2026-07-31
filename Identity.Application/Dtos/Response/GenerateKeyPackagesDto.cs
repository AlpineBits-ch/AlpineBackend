namespace Identity.Application.Dtos.Response;

public class GenerateKeyPackagesDto
{
    /// <summary>How many single-use key packages to generate and upload.</summary>
    public int Count { get; init; }

    /// <summary>True when this device has no unexpired last-resort package.</summary>
    public bool NeedsLastResort { get; init; }
}
