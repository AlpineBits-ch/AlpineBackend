using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Storage for sealed payment handles.</summary>
public class PaymentHandleService(MicroserviceContext ctx)
{
    /// <summary>Generous for a handful of account numbers and labels, small enough that the table
    /// cannot be used as free storage. Deliberately a bound on the sealed payload and not on
    /// anything inside it - "too many handles" is not a sentence this service can complete.</summary>
    public const int MaxCiphertextBytes = 8 * 1024;

    public const int MaxNonceBytes = 64;

    /// <summary>One wrap per device of every member.</summary>
    public const int MaxWraps = 200;

    public const int MaxWrappedKeyBytes = 1024;

    public const int MaxDeviceIdLength = 128;

    /// <summary>
    /// Returns the reason a seal request is unacceptable, or null when it is fine.
    /// </summary>
    public static string? Validate(SealPaymentHandlesDto dto, IReadOnlySet<string> memberUserIds)
    {
        if (dto.Ciphertext.Length == 0) return "Ciphertext is required";
        if (dto.Ciphertext.Length > MaxCiphertextBytes)
            return $"Ciphertext must be {MaxCiphertextBytes} bytes or fewer";

        if (dto.Nonce.Length == 0) return "Nonce is required";
        if (dto.Nonce.Length > MaxNonceBytes) return $"Nonce must be {MaxNonceBytes} bytes or fewer";

        if (dto.Version <= 0) return "Version must be greater than zero";

        if (dto.Wraps.Count > MaxWraps) return $"At most {MaxWraps} wraps may be sent";

        var seenDevices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wrap in dto.Wraps)
        {
            if (string.IsNullOrWhiteSpace(wrap.RecipientUserId) || string.IsNullOrWhiteSpace(wrap.RecipientDeviceId))
                return "Every wrap needs a recipient user and device";

            if (wrap.RecipientDeviceId.Length > MaxDeviceIdLength)
                return $"A device id must be {MaxDeviceIdLength} characters or fewer";

            // The identity rule that matters.
            if (!memberUserIds.Contains(wrap.RecipientUserId))
                return "Every wrap recipient must be a member of this guild";

            // Deduplicated because the device is half the row's key, so a repeat would surface as a
            // constraint violation and a 500 rather than as the client bug it is.
            if (!seenDevices.Add(wrap.RecipientDeviceId)) return "A device may only be wrapped once";

            if (wrap.WrappedKey.Length == 0) return "Every wrap needs a key";
            if (wrap.WrappedKey.Length > MaxWrappedKeyBytes)
                return $"A wrapped key must be {MaxWrappedKeyBytes} bytes or fewer";
        }

        // Deliberately not checked: whether each device is still registered to its recipient.
        return null;
    }

    /// <summary>Writes the caller's sealed payload, replacing anything already there.</summary>
    public async Task<PaymentHandleBlob> SealAsync(
        string guildId, string userId, SealPaymentHandlesDto dto, int rosterVersion)
    {
        var blob = await ctx.Set<PaymentHandleBlob>()
            .Include(b => b.Wraps)
            .FirstOrDefaultAsync(b => b.GuildId == guildId && b.UserId == userId);

        if (blob is null)
        {
            blob = PaymentHandleBlob.Create(
                guildId, userId, dto.Ciphertext, dto.Nonce, dto.Version, rosterVersion);
            ctx.Set<PaymentHandleBlob>().Add(blob);
        }
        else
        {
            blob.Ciphertext = dto.Ciphertext;
            blob.Nonce = dto.Nonce;
            blob.Version = dto.Version;
            blob.MemberRosterVersion = rosterVersion;
            blob.UpdatedAt = DateTimeOffset.UtcNow;

            ctx.Set<PaymentHandleKeyWrap>().RemoveRange(blob.Wraps);
            blob.Wraps.Clear();
        }

        foreach (var wrap in dto.Wraps)
        {
            blob.Wraps.Add(new PaymentHandleKeyWrap
            {
                PaymentHandleBlobId = blob.Id,
                RecipientUserId = wrap.RecipientUserId,
                RecipientDeviceId = wrap.RecipientDeviceId,
                WrappedKey = wrap.WrappedKey,
            });
        }

        return blob;
    }

    /// <summary>
    /// Drops the caller's own blob and every wrap of it, returning whether there was one.
    /// </summary>
    public async Task<bool> DeleteAsync(string guildId, string userId)
    {
        var blob = await ctx.Set<PaymentHandleBlob>()
            .Include(b => b.Wraps)
            .FirstOrDefaultAsync(b => b.GuildId == guildId && b.UserId == userId);
        if (blob is null) return false;

        ctx.Set<PaymentHandleKeyWrap>().RemoveRange(blob.Wraps);
        ctx.Set<PaymentHandleBlob>().Remove(blob);

        return true;
    }

    /// <summary>
    /// Which of <paramref name="memberUserIds"/> have agreed to show their phone number to this
    /// guild.
    /// </summary>
    public async Task<List<string>> GetPhoneSharingMemberIdsAsync(
        string guildId, IReadOnlyCollection<string> memberUserIds)
    {
        if (memberUserIds.Count == 0) return [];

        return await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId
                        && m.SharePhoneForPayments
                        && memberUserIds.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync();
    }

    /// <summary>Whether one member has opted in, for echoing the caller's own state back to
    /// them.</summary>
    public async Task<bool> IsSharingPhoneAsync(string guildId, string userId) =>
        await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.UserId == userId)
            .Select(m => m.SharePhoneForPayments)
            .FirstOrDefaultAsync();

    /// <summary>
    /// Sets the caller's own opt-in, returning false when there is no member row to set it on.
    /// </summary>
    public async Task<bool> SetPhoneSharingAsync(string guildId, string userId, bool share)
    {
        var member = await ctx.GuildMembers
            .FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);
        if (member is null) return false;

        member.SharePhoneForPayments = share;
        member.UpdatedAt = DateTimeOffset.UtcNow;

        return true;
    }

    /// <summary>
    /// Every current member's sealed handles, carrying the content key for exactly one device.
    /// </summary>
    public async Task<List<SealedPaymentHandlesDto>> ReadForDeviceAsync(
        string guildId, string deviceId, IReadOnlyCollection<string> memberUserIds)
    {
        var blobs = await ctx.Set<PaymentHandleBlob>().AsNoTracking()
            .Where(b => b.GuildId == guildId && memberUserIds.Contains(b.UserId))
            .ToListAsync();

        if (blobs.Count == 0) return [];

        var blobIds = blobs.Select(b => b.Id).ToList();

        var wraps = await ctx.Set<PaymentHandleKeyWrap>().AsNoTracking()
            .Where(w => w.RecipientDeviceId == deviceId && blobIds.Contains(w.PaymentHandleBlobId))
            .ToDictionaryAsync(w => w.PaymentHandleBlobId, w => w.WrappedKey);

        return blobs
            .OrderBy(b => b.UserId, StringComparer.Ordinal)
            .Select(b => new SealedPaymentHandlesDto
            {
                UserId = b.UserId,
                Ciphertext = b.Ciphertext,
                Nonce = b.Nonce,
                Version = b.Version,
                MemberRosterVersion = b.MemberRosterVersion,
                UpdatedAt = b.UpdatedAt,
                WrappedKey = wraps.GetValueOrDefault(b.Id),
            })
            .ToList();
    }
}
