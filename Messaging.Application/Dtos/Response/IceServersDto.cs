namespace Messaging.Application.Dtos.Response;



public class IceServersDto
{
    public List<IceServerDto> IceServers { get; set; } = [];
}

public class IceServerDto
{
    public List<string> Urls { get; set; } = [];

    public string? Username { get; set; }

    public string? Credential { get; set; }
}