using Facet;

namespace Identity.Contracts.Dto.Response;


public  class ApplicationUserDto
{
    public string Id { get; set; }
    public string Email { get; set; }
    public string? SteamId { get; set; }
    public string? UserName { get; set; }
    public bool IsBot { get; set; }
    public bool EmailConfirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}