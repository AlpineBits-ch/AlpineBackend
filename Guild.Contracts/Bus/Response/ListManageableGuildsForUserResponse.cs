namespace Guild.Contracts.Bus.Response;

public class ListManageableGuildsForUserResponse
{
    public List<ManageableGuildSummary> Guilds { get; set; } = new();
}

public class ManageableGuildSummary
{
    public string Id { get; set; }
    public string Name { get; set; }
}
