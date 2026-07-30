namespace Guild.Application.Dtos.Request;

public class UpdateOnboardingConfigDto
{
    public bool Enabled { get; set; }
    public string? RulesText { get; set; }
    public List<string> DefaultChannelIds { get; set; } = [];
}
