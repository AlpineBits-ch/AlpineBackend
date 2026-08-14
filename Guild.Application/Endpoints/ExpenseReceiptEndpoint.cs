using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>Photos of the till receipts behind ledger expenses.</summary>
[Authorize]
public class ExpenseReceiptEndpoint
{
    /// <summary>A shop with a long receipt gets photographed in two or three frames; past four it
    /// is somebody using the ledger as a photo album, and every one of them is fetched on the
    /// expense screen.</summary>
    private const int MaxReceiptsPerExpense = 4;

    /// <summary>Comfortably above a phone photo and below anything that would make the expense
    /// screen unusable on mobile data.</summary>
    private const long MaxReceiptBytes = 8 * 1024 * 1024;

    /// <summary>An allowlist rather than a blocklist, and the stored value comes from here rather
    /// than from the request. The presigned URL serves the object with the content type we recorded,
    /// so recording the caller's own string would let an upload choose how a browser interprets its
    /// own bytes.</summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif", "application/pdf",
    };

    private const int PresignedUrlMinutes = 10;

    private static string StorageKey(ExpenseReceipt receipt) =>
        $"receipts/{receipt.GuildId}/{receipt.ExpenseId}/{receipt.Id}";

    private static string? PresignedUrl(IAmazonS3 s3, string storageKey)
    {
        var config = Env.StorageConfiguration;

        return s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = config.BucketName,
            Key = storageKey,
            Expires = DateTime.UtcNow.AddMinutes(PresignedUrlMinutes),
            Verb = HttpVerb.GET,
        });
    }

    private static ExpenseReceiptDto ToDto(ExpenseReceipt receipt, IAmazonS3 s3) => new()
    {
        Id = receipt.Id,
        ExpenseId = receipt.ExpenseId,
        FileName = receipt.FileName,
        ContentType = receipt.ContentType,
        SizeBytes = receipt.SizeBytes,
        UploadedByUserId = receipt.UploadedByUserId,
        UploadedAt = receipt.CreatedAt,
        Url = PresignedUrl(s3, receipt.StorageKey),
    };

    /// <summary>Correcting or annotating your own expense needs only <see cref="ModulePermissions.AddExpenses"/>;
    /// attaching evidence to one somebody else entered changes what their record says, so it is a
    /// moderator action - the same split the expense edit and delete routes already use.</summary>
    private static ModulePermissions RequiredFor(Expense expense, string userId) =>
        expense.CreatedByUserId == userId ? ModulePermissions.AddExpenses : ModulePermissions.ManageLedger;

    [WolverineGet("/api/v1/expenses/{expenseId}/receipts")]
    public async Task<IResult> ListAsync(string expenseId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] IAmazonS3 s3, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var expense = await ctx.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expenseId);
        if (expense is null) return Results.NotFound();

        // Reading is plain channel visibility: a receipt is part of the expense, and anybody who
        // can see the amount can see what it was for.
        var access = await household.ResolveAsync(
            expense.ChannelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var receipts = await ctx.ExpenseReceipts.AsNoTracking()
            .Where(r => r.ExpenseId == expenseId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync();

        return Results.Ok(receipts.Select(r => ToDto(r, s3)));
    }

    [WolverinePost("/api/v1/expenses/{expenseId}/receipts")]
    public async Task<IResult> UploadAsync(string expenseId, IFormFile file,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] IAmazonS3 s3, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var expense = await ctx.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == expenseId);
        if (expense is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            expense.ChannelId, ChannelType.Ledger, userId, RequiredFor(expense, userId));
        if (access.ToFailure() is { } failure) return failure;

        if (file is null || file.Length == 0) return Results.BadRequest("A file is required");
        if (file.Length > MaxReceiptBytes)
            return Results.BadRequest($"A receipt must be {MaxReceiptBytes / (1024 * 1024)} MB or smaller");

        var contentType = AllowedContentTypes.FirstOrDefault(
            t => string.Equals(t, file.ContentType, StringComparison.OrdinalIgnoreCase));
        if (contentType is null)
            return Results.BadRequest("A receipt must be a JPEG, PNG, WebP, HEIC image or a PDF");

        var existing = await ctx.ExpenseReceipts.CountAsync(r => r.ExpenseId == expenseId);
        if (existing >= MaxReceiptsPerExpense)
            return Results.BadRequest($"An expense may have at most {MaxReceiptsPerExpense} receipts");

        var receipt = ExpenseReceipt.Create(
            expenseId, expense.GuildId, expense.ChannelId, storageKey: "",
            SafeFileName(file.FileName), contentType, file.Length, userId);

        receipt.StorageKey = StorageKey(receipt);

        // Uploaded before the row is written, because the two orders fail differently: an object
        // with no row is invisible and costs a few kilobytes, while a row with no object is a
        // permanently broken thumbnail on somebody's expense.
        await using (var stream = file.OpenReadStream())
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Env.StorageConfiguration.BucketName,
                Key = receipt.StorageKey,
                ContentType = contentType,
                InputStream = stream,
            });
        }

        ctx.ExpenseReceipts.Add(receipt);

        auditLog.Log(expense.GuildId, userId, AuditActionType.ExpenseUpdated, expense.Id,
            new { ReceiptId = receipt.Id, ReceiptAdded = true });

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(expense.GuildId, expense.ChannelId, "guild.ExpenseReceiptAdded",
            new
            {
                GuildId = expense.GuildId,
                ChannelId = expense.ChannelId,
                ExpenseId = expenseId,
                Receipt = ToDto(receipt, s3),
            });

        return Results.Ok(ToDto(receipt, s3));
    }

    [WolverineDelete("/api/v1/receipts/{receiptId}")]
    public async Task<IResult> DeleteAsync(string receiptId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] IAmazonS3 s3, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var receipt = await ctx.ExpenseReceipts.FirstOrDefaultAsync(r => r.Id == receiptId);
        if (receipt is null) return Results.NotFound();

        var expense = await ctx.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == receipt.ExpenseId);
        if (expense is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            receipt.ChannelId, ChannelType.Ledger, userId, RequiredFor(expense, userId));
        if (access.ToFailure() is { } failure) return failure;

        var storageKey = receipt.StorageKey;

        ctx.ExpenseReceipts.Remove(receipt);

        auditLog.Log(receipt.GuildId, userId, AuditActionType.ExpenseUpdated, receipt.ExpenseId,
            new { ReceiptId = receiptId, ReceiptRemoved = true });

        await ctx.SaveChangesAsync();

        // After the commit and best-effort, in that order: the row going is the act the caller
        // asked for, and a storage hiccup must not turn "take that photo down" into an error the
        // user retries against a receipt that is already gone.
        try
        {
            await s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Env.StorageConfiguration.BucketName,
                Key = storageKey,
            });
        }
        catch (AmazonS3Exception)
        {
            // Swallowed deliberately - see above.
        }

        await household.BroadcastAsync(receipt.GuildId, receipt.ChannelId, "guild.ExpenseReceiptDeleted",
            new
            {
                GuildId = receipt.GuildId,
                ChannelId = receipt.ChannelId,
                ExpenseId = receipt.ExpenseId,
                ReceiptId = receiptId,
            });

        return Results.NoContent();
    }

    /// <summary>Keeps the leaf name and drops any path the client sent with it.</summary>
    private static string SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "receipt";

        var leaf = fileName.Split('/', '\\')[^1].Trim();

        return string.IsNullOrEmpty(leaf) ? "receipt" : leaf[..Math.Min(leaf.Length, 100)];
    }
}
