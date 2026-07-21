using Isle.Domain.Entity;

namespace Isle.Api.Chat.CommandController;


public class CommandContext
{
    public string PlayerName { get; set; }
    public string PlayerSteam { get; set; }
    
    public string PlayerSpecies { get; set; }
    public DinoHealthData HealthData { get; set; }
    
    public ICollection<string> Arguments { get; set; }
    
    public string PlayerId { get; set; }
    
    public bool IsAdmin { get; set; }
    
}

public abstract class ChatCommand
{
    public abstract Task<string> ExecuteAsync(CommandContext context);
    public abstract string Name { get; }
    public abstract string Description { get;  }
    public abstract bool IsAdminOnly { get; set; } 
    
}