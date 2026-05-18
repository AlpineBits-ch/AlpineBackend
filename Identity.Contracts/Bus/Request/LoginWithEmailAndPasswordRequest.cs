namespace Identity.Contracts.Bus.Request;

public class LoginWithEmailAndPasswordRequest
{
    public string Email { get; set; }
    public string Password { get; set; }
}