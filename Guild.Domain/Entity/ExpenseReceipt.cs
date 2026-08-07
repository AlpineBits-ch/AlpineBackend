using Persistence;

namespace Guild.Domain.Entity;

/// <summary>A photo of the till receipt behind an <see cref="Expense"/>.</summary>
public class ExpenseReceipt : BaseEntity<ExpenseReceipt>, IPrefixedEntity
{
    public static string Prefix { get; } = "rcpt";

    public string ExpenseId { get; set; } = null!;
    public virtual Expense Expense { get; set; } = null!;

    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;

    /// <summary>The object-storage key.</summary>
    public string StorageKey { get; set; } = null!;

    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }

    public string UploadedByUserId { get; set; } = null!;

    public static ExpenseReceipt Create(
        string expenseId, string guildId, string channelId, string storageKey,
        string fileName, string contentType, long sizeBytes, string uploadedByUserId)
    {
        var date = DateTimeOffset.UtcNow;
        return new ExpenseReceipt
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ExpenseId = expenseId,
            GuildId = guildId,
            ChannelId = channelId,
            StorageKey = storageKey,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            UploadedByUserId = uploadedByUserId,
        };
    }
}
