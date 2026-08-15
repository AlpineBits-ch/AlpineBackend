using System.ComponentModel.DataAnnotations.Schema;
using Persistence;

namespace Identity.Domain.Entities;

/// <summary>The record that one billing notification has already been mailed.</summary>
public class BillingNotificationRecord : BaseEntity<BillingNotificationRecord>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "bnot";

    /// <summary>The transition's stable identity, minted by the Billing endpoint that raised the
    /// intent. See the contracts in <c>Identity.Contracts.Bus.Commands</c>.</summary>
    public string DedupeKey { get; set; } = null!;

    /// <summary>The account that was mailed.</summary>
    public string UserId { get; set; } = null!;

    /// <summary>The contract type's name, for the operator reading this table.</summary>
    public string Kind { get; set; } = null!;

    public DateTimeOffset SentAt { get; set; }

    public static BillingNotificationRecord Create(
        string dedupeKey, string userId, string kind, DateTimeOffset sentAt) => new()
    {
        Id = GenerateId(),
        CreatedAt = sentAt,
        UpdatedAt = sentAt,
        DedupeKey = dedupeKey,
        UserId = userId,
        Kind = kind,
        SentAt = sentAt,
    };
}
