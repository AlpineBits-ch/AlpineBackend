namespace Social.Contracts.Dtos;

public enum RelationshipStatus
{
    None,
    Pending,
    Accepted,
    Rejected,
    Blocked
}

public class RelationshipDto
{
    public string Id { get; set; }
    public string ProfileId { get; set; }
    public string UserId { get; init; }
    public RelationshipStatus Status { get; set; }
}