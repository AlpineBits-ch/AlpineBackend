namespace Guild.Application.Dtos.Request;

/// <summary>Body of <c>PUT /api/v1/guilds/{guildId}/privacy</c>.</summary>
public class UpdateGuildPrivacyDto
{
    public bool AllowDirectMessages { get; set; }
}
