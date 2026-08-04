namespace Social.Contracts.Bus.Integration.Request;

public class GetProfileByUserIdRequest
{
    public string UserId { get; set; }

    /// <summary>
    /// Who the profile is being resolved *for*, when the caller is acting on a specific user's
    /// behalf (privacy spec T2-17: "so other services cannot route around them").
    ///
    /// <para>Optional and additive - an existing caller that omits it is treated as a stranger, which
    /// is the fail-closed reading: the response then carries no privacy-gated field at all. Set it
    /// whenever the profile is on its way to a particular person's screen, or that person will see
    /// less than they are entitled to.</para>
    /// </summary>
    public string? ViewerUserId { get; set; }
}

public class GetProfileByUserIdsRequest
{
    public ICollection<string> UserIds { get; set; }

    /// <inheritdoc cref="GetProfileByUserIdRequest.ViewerUserId"/>
    public string? ViewerUserId { get; set; }
}
