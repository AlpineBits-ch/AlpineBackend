namespace Isle.Domain.Exceptions;

public class StorageFullException(string storageId, int maxSlots) : Exception
{
    public string StorageId { get; set; } = storageId;
    public int MaxSlots { get; set; } = maxSlots;
}