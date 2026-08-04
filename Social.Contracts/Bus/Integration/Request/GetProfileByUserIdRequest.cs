namespace Social.Contracts.Bus.Integration.Request;

public class GetProfileByUserIdRequest
{
    public string UserId { get; set; }

    /// <summary>
    /// Who the profile is being resolved for, when the caller is acting on a specific user's behalf
    /// (privacy spec T2-17: "so other services cannot route around them").
    /// </summary>
    public string? ViewerUserId { get; set; }
}

public class GetProfileByUserIdsRequest
{
    public ICollection<string> UserIds { get; set; }

    /// <inheritdoc cref="GetProfileByUserIdRequest.ViewerUserId"/>
    public string? ViewerUserId { get; set; }
}
