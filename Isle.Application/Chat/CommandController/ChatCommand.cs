using Isle.Domain.Entity;

namespace Isle.Api.Chat.CommandController;


public class CommandContext
{
    public string PlayerName { get; set; }
    public string PlayerSteam { get; set; }

    /// <summary>Species of the player's active dino, or null when they have no live pawn.</summary>
    public string? PlayerSpecies { get; set; }

    /// <summary>Growth (0..1) of the player's active dino; 0 when they have no live pawn.</summary>
    public double PlayerGrowth { get; set; }
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

    /// <summary>Per-player cooldown between successful runs of this command.</summary>
    public virtual TimeSpan Cooldown => TimeSpan.Zero;

    public virtual bool CanRun(CommandContext context)
    {
        if(IsAdminOnly && !context.IsAdmin)
            return false;

        return true;
    }
    
}