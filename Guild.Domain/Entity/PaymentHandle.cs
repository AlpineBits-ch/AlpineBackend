using Persistence;

namespace Guild.Domain.Entity;

/// <summary>One housemate's payment details, sealed.</summary>
public class PaymentHandleBlob : BaseEntity<PaymentHandleBlob>, IPrefixedEntity
{
    public static string Prefix { get; } = "phnd";

    public string GuildId { get; set; } = null!;

    /// <summary>Whose details these are.</summary>
    public string UserId { get; set; } = null!;

    /// <summary>The whole handle set as one sealed payload.</summary>
    public byte[] Ciphertext { get; set; } = null!;

    public byte[] Nonce { get; set; } = null!;

    /// <summary>Which crypto envelope produced <see cref="Ciphertext"/>.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The guild's membership as it stood when this blob was last sealed - see
    /// <c>PaymentHandleEndpoint</c> for how it is derived.
    /// </summary>
    public int MemberRosterVersion { get; set; }

    public virtual ICollection<PaymentHandleKeyWrap> Wraps { get; set; } = [];

    public static PaymentHandleBlob Create(
        string guildId, string userId, byte[] ciphertext, byte[] nonce, int version, int memberRosterVersion)
    {
        var date = DateTimeOffset.UtcNow;
        return new PaymentHandleBlob
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = guildId,
            UserId = userId,
            Ciphertext = ciphertext,
            Nonce = nonce,
            Version = version,
            MemberRosterVersion = memberRosterVersion,
        };
    }
}

/// <summary>The content key of one <see cref="PaymentHandleBlob"/>, sealed to one device.</summary>
public class PaymentHandleKeyWrap
{
    public string PaymentHandleBlobId { get; set; } = null!;
    public virtual PaymentHandleBlob Blob { get; set; } = null!;

    /// <summary>Carried alongside the device id so the write path can check the recipient against
    /// the guild roster without a lookup into a service that does not own the roster.</summary>
    public string RecipientUserId { get; set; } = null!;

    /// <summary>Client device id, as resolved from <c>X-Device-Id</c>.</summary>
    public string RecipientDeviceId { get; set; } = null!;

    public byte[] WrappedKey { get; set; } = null!;
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<PaymentHandleBlob>(blobBuilder =>
// {
//     blobBuilder.HasOne<Domain.Aggregates.Guild>()
//         .WithMany()
//         .HasForeignKey(x => x.GuildId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // One sealed payload per person per guild; the write path upserts on this.
//     blobBuilder.HasIndex(x => new { x.GuildId, x.UserId }).IsUnique();
// });
//
// modelBuilder.Entity<PaymentHandleKeyWrap>(wrapBuilder =>
// {
//     wrapBuilder.HasKey(x => new { x.PaymentHandleBlobId, x.RecipientDeviceId });
//
//     wrapBuilder.HasOne(x => x.Blob)
//         .WithMany(x => x.Wraps)
//         .HasForeignKey(x => x.PaymentHandleBlobId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // The read path is "every blob in this guild, wraps for this one device".
//     wrapBuilder.HasIndex(x => x.RecipientDeviceId);
// });
//
// DbSet: public DbSet<PaymentHandleBlob> PaymentHandleBlobs { get; set; }
// DbSet: public DbSet<PaymentHandleKeyWrap> PaymentHandleKeyWraps { get; set; }
//
// No MapEnum is needed: nothing on either entity is an enum any more, which is the point.
// ExpenseReceipt's configuration and MapEnum<ExpenseCategory>() are already in the context.
