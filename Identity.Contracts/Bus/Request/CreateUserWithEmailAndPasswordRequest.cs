namespace Identity.Contracts.Bus.Request;

public class CreateUserWithEmailAndPasswordRequest
{
    public string Email { get; set; }
    public string Password { get; set; }
    
    public DateOnly BirthDate { get; set; }
    
    public string Username { get; set; }

    /// <summary>
    /// Address the registration came from, stamped onto the Terms/Privacy consent records the
    /// signup writes (T1-10).
    /// </summary>
    public string? IpAddress { get; set; }
}