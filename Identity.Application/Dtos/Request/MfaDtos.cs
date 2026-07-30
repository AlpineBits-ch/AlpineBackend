namespace Identity.Application.Dtos.Request;

public class EnableMfaDto
{
    public string Code { get; set; } = null!;
}

public class DisableMfaDto
{
    public string Password { get; set; } = null!;
}

public class RegenerateMfaRecoveryCodesDto
{
    public string Password { get; set; } = null!;
}
