namespace Isle.Domain.Enums;

public enum GameModeState
{
    Idle,       // definition exists, not currently running
    Queuing,    // players gathering/joining before start (optional — skip if you don't need a queue phase)
    Running,    // event is live
    Resolving,  // event ended, standings/rewards being finalized
    Cooldown    // finished, waiting out Definition.Cooldown before it can trigger again
}