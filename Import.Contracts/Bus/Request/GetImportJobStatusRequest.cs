namespace Import.Contracts.Bus.Request;

public class GetImportJobStatusRequest
{
    public string ImportJobId { get; set; }
}

public class GetImportJobStatusResponse
{
    public string ImportJobId { get; set; }
    public string Status { get; set; }
    public string? EchoGuildId { get; set; }
    public string? ErrorMessage { get; set; }
}
