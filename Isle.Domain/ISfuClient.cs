namespace Isle.Api;

public interface ISfuClient
{
    Task<string?> GetActiveTrackId(string playerId);

    Task SubscribeMutual(string playerIdA, string playerIdB);
    Task UnsubscribeAll(string playerId, string cellId);
    Task BroadcastPosition(string playerId, IReadOnlyList<string> recipients, float x, float y, float z);
}