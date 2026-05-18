namespace Identity.Contracts.Commands;

public class CreateUserCommand
{
    /// <summary>
    /// Specified by the fk user id from our auth provider
    /// </summary>
    public string Id { get; set; }
    public string Email { get; set; }
    public DateOnly BirthDate { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
}

public class CreateUserResponse
{
    public string? UserId { get; set; }
}