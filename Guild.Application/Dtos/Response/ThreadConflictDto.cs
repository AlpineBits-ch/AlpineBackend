namespace Guild.Application.Dtos.Response;

/// <summary>Returned with 409 when a message already carries a thread, so the client can open the
/// one that exists rather than telling the user to try again.</summary>
public class ThreadConflictDto
{
    /// <summary>The thread already hanging off the message.</summary>
    public string? ThreadId { get; set; }
}
