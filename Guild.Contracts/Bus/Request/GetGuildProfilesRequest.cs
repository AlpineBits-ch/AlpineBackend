namespace Guild.Contracts.Bus.Request;

public class GetGuildProfilesRequest
{
    public IReadOnlyList<string> GuildIds { get; set; } = [];
}

public class GetGuildProfilesResponse
{
    public IReadOnlyList<GuildProfileDto> Profiles { get; set; } = [];
}

public class GuildProfileDto
{
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public string? BannerUrl { get; set; }
    public int MemberCount { get; set; }
    public int ActiveMemberCount { get; set; }
    public string Features { get; set; } = string.Empty;
    public string PrimaryLanguage { get; set; } = "en";
    public IReadOnlyList<string> OtherLanguages { get; set; } = [];
}
