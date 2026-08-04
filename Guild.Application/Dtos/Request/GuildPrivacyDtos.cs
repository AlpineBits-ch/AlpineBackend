namespace Guild.Application.Dtos.Request;

/// <summary>
/// Body of <c>PUT /api/v1/guilds/{guildId}/privacy</c>.
///
/// <para>A PUT with one required field rather than the PATCH shape the notification settings use:
/// there is exactly one knob here, so "omitted means leave alone" would only ever describe a
/// request that asks for nothing.</para>
/// </summary>
public class UpdateGuildPrivacyDto
{
    public bool AllowDirectMessages { get; set; }
}
