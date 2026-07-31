using Messaging.Domain.Enums;

namespace Messaging.Application.Dtos.Request;

public class CreateConversationMemberDto
{
    public string UserId { get; set; }
}

public class CreateConversationDto
{
    public ChannelEncryptionState Encryption { get; set; }
    
    public List<CreateConversationMemberDto> Members { get; set; } = new();
    
    public string? Name { get; set; }
    
    public List<DeviceWelcomeDto> DeviceWelcomes { get; set; } = new();
    
    public byte[]? MlsGroupId { get; set; }
    public long? MlsEpoch { get; set; }
    public byte[]? MlsGroupInfo { get; set; }
}

public class DeviceWelcomeDto
{
    public string DeviceId { get; set; }
    public byte[] Welcome { get; set; }
    public string UserId { get; set; }
}
/// <summary>Adds one person to an existing group conversation.</summary>
public class AddConversationMemberDto
{
    public string UserId { get; set; } = null!;
}
