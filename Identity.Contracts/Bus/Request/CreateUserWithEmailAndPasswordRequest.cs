namespace Identity.Contracts.Bus.Request;

public class CreateUserWithEmailAndPasswordRequest
{
    public string Email { get; set; }
    public string Password { get; set; }
    
    public DateOnly BirthDate { get; set; }
    
    public string Username { get; set; }

    /// <summary>
    /// Address the registration came from, stamped onto the Terms/Privacy consent records the signup
    /// writes (T1-10).
    ///
    /// <para>Carried on the request rather than read at the handler because the handler runs off a
    /// queue, where there is no HTTP context to read it from - and a consent record whose origin is
    /// the address of a Wolverine worker is worse than one with no address at all, because it looks
    /// like evidence. Nullable: a caller that genuinely does not have one records the consent
    /// without it rather than not recording the consent.</para>
    /// </summary>
    public string? IpAddress { get; set; }
}