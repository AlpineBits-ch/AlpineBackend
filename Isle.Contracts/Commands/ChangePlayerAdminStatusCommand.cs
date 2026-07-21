namespace Isle.Contracts.Commands;

public class ChangePlayerAdminStatusCommand
{
    public string PlayerId { get; set; }
    public bool IsAdmin { get; set; }
}