namespace Guild.Application.Dtos.Request;

public class CreateGuildDto
{
    public string Name { get; set; }
    public string? Description { get; set; }
    
    public override string ToString()
    {
        return $"Guild: {Name}, Description: {Description}";
    }
}