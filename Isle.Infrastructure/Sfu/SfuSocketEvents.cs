namespace Isle.Contracts.Events.Voice;

public static class SfuSocketEvents
{
    private const string Prefix = "isle.";
    public const string SubscribeMutual = Prefix + "SubscribeMutual";
    public const string UnsubscribeAll  = Prefix + "UnsubscribeAll";
    public const string PlayerPosition  = Prefix + "PlayerPosition";
    public const string SelfPosition    = Prefix + "SelfPosition";
}