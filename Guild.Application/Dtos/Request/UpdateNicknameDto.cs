namespace Guild.Application.Dtos.Request;

public class UpdateNicknameDto
{
    /// <summary>The new nickname, or null/empty to clear it and fall back to the account
    /// username. Trimmed and length-checked at the endpoint (1-32 characters once trimmed).</summary>
    public string? Nickname { get; set; }
}
