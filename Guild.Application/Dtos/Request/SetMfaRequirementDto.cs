namespace Guild.Application.Dtos.Request;

/// <summary>Body of the owner-only MFA requirement switch.</summary>
public class SetMfaRequirementDto
{
    public bool Required { get; set; }
}
