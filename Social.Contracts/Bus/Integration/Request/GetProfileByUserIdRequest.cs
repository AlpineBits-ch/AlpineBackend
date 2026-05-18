namespace Social.Contracts.Bus.Integration.Request;

public class GetProfileByUserIdRequest
{
    public string UserId { get; set; }
}

public class GetProfileByUserIdsRequest
{
    public ICollection<string> UserIds { get; set; }
}