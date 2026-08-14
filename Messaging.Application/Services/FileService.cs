using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using AppEnvironment;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Messaging.Domain.Entities;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Messaging.Application.Services;

public class UploadedFile
{
    public string Id { get; init; }
    public string Url { get; set; }
    public string FileName { get; set; }
    public long SizeBytes { get; set; }
    public string ContentType { get; set; }
}

/// <summary>Whose ceilings apply to one upload, and whose quota it consumes.</summary>
public readonly record struct StorageUploadContext
{
    private StorageUploadContext(string? userId, string? guildId)
    {
        UserId = userId;
        GuildId = guildId;
    }

    /// <summary>Who is uploading.</summary>
    public string? UserId { get; }

    /// <summary>The guild whose quota this consumes, or null for anything the person does as
    /// themselves.</summary>
    public string? GuildId { get; }

    public static StorageUploadContext ForUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new StorageUploadContext(userId, null);
    }

    public static StorageUploadContext ForGuild(string guildId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new StorageUploadContext(userId, guildId);
    }
}

/// <summary>Which of the two storage limits refused a file.</summary>
public enum StorageRefusalCode
{
    /// <summary>The file is larger than the effective single-upload ceiling.</summary>
    UploadTooLarge,

    /// <summary>The guild is at or over its storage quota.</summary>
    GuildQuotaExhausted,
}

/// <summary>One file that was not stored, carrying everything the wire needs to say why.</summary>
/// <param name="FileName">As the client sent it, so a log or an admin can name the file.</param>
/// <param name="SizeBytes">What was asked for.</param>
/// <param name="Code">Which limit refused it.</param>
/// <param name="Key">The entitlement key that bound.</param>
/// <param name="LimitBytes">
/// The effective limit, or <see cref="EntitlementValue.Unlimited"/> when there was none.
/// </param>
/// <param name="Cause">Which side bound, in the reason vocabulary.</param>
/// <param name="BoundBy">
/// <see cref="EntitlementBoundBy"/>, or null for an operator ceiling, which is not a subject
/// anybody can upgrade.
/// </param>
public sealed record RejectedUpload(
    string FileName,
    long SizeBytes,
    StorageRefusalCode Code,
    EntitlementKey Key,
    long LimitBytes,
    EntitlementDegradationReason Cause,
    string? BoundBy,
    EntitlementDegradation Degradation);

/// <summary>What one upload request actually produced.</summary>
public sealed record FileUploadResult(
    IReadOnlyList<UploadedFile> Uploaded,
    IReadOnlyList<RejectedUpload> Rejected)
{
    public static readonly FileUploadResult Nothing = new([], []);

    /// <summary>True when the request was refused outright rather than reduced.</summary>
    public bool Refused => Uploaded.Count == 0 && Rejected.Count > 0;
}

/// <summary>The handful of object-store operations the upload path needs.</summary>
public interface IAttachmentObjectStore
{
    Task PutAsync(
        string bucketName, string key, string? contentType, Stream content, CancellationToken ct);
}

public sealed class S3AttachmentObjectStore(IAmazonS3 s3Client) : IAttachmentObjectStore
{
    public Task PutAsync(
        string bucketName, string key, string? contentType, Stream content, CancellationToken ct) =>
        s3Client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                ContentType = contentType,
                InputStream = content,
            }, ct);
}

/// <summary>Running per-guild storage usage, in bytes.</summary>
public interface IGuildStorageLedger
{
    Task<long> GetUsedBytesAsync(string guildId, CancellationToken ct);

    /// <summary>Adds a signed delta and returns the new total.</summary>
    Task<long> AddAsync(string guildId, long deltaBytes, CancellationToken ct);
}

/// <summary>The ledger over the Redis connection Messaging already holds.</summary>
public sealed class RedisGuildStorageLedger(
    IConnectionMultiplexer redis, ILogger<RedisGuildStorageLedger> logger) : IGuildStorageLedger
{
    /// <summary>Anything reading these keys from outside must treat the form as storage format. When
    /// Billing arrives it rolls these up rather than replacing them.</summary>
    public static string UsageKey(string guildId) => $"storage:usage:guild:{guildId}";

    public async Task<long> GetUsedBytesAsync(string guildId, CancellationToken ct)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(UsageKey(guildId));
            return value.TryParse(out long used) && used > 0 ? used : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Storage quota unreadable for guild {GuildId}; treating usage as zero", guildId);
            return 0;
        }
    }

    public async Task<long> AddAsync(string guildId, long deltaBytes, CancellationToken ct)
    {
        try
        {
            var db = redis.GetDatabase();
            var total = await db.StringIncrementAsync(UsageKey(guildId), deltaBytes);

            // A release can only ever be for bytes that were counted, so a negative total means the
            // counter and the bucket have already diverged - most likely bytes that predate this
            // accounting.
            if (total < 0)
            {
                await db.StringSetAsync(UsageKey(guildId), 0);
                return 0;
            }

            return total;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Storage quota not updated for guild {GuildId} by {Delta} bytes", guildId, deltaBytes);
            return 0;
        }
    }
}

/// <summary>The ledger used when a host has registered none.</summary>
public sealed class UnmeteredGuildStorageLedger : IGuildStorageLedger
{
    public static readonly UnmeteredGuildStorageLedger Instance = new();

    public Task<long> GetUsedBytesAsync(string guildId, CancellationToken ct) => Task.FromResult(0L);

    public Task<long> AddAsync(string guildId, long deltaBytes, CancellationToken ct) => Task.FromResult(0L);
}

/// <summary>
/// The storage choke point: every attachment that enters this instance passes through <see
/// cref="UploadFileAsync"/>, which is why <c>storage.upload_max_bytes</c>,
/// <c>user.upload_max_bytes</c> and <c>storage.guild_quota_bytes</c> are enforced here and nowhere
/// else (spec section 4.4).
/// </summary>
public class FileService
{
    /// <summary>
    /// What a single upload is capped at when nothing else has an opinion: no plan configured, no
    /// operator ceiling set, entitlement resolving to unlimited.
    /// </summary>
    public const long InstanceDefaultUploadCeilingBytes = 1024L * 1024L * 35L;

    private readonly IAttachmentObjectStore _objects;
    private readonly EntitlementResolver? _entitlements;
    private readonly OperatorCeilings _operatorCeilings;
    private readonly IGuildStorageLedger _ledger;
    private readonly TimeProvider _clock;

    /// <param name="s3Client">The bucket everything lands in.</param>
    /// <param name="operatorCeilings">Null means this box sets no ceilings of its own, which is the
    /// shipped state of every deployment.</param>
    /// <param name="storageLedger">Null leaves <c>storage.guild_quota_bytes</c> unenforced; see
    /// <see cref="UnmeteredGuildStorageLedger"/>.</param>
    /// <param name="entitlements">Null resolves every key to its catalogue default, which is
    /// unlimited, which is today's behaviour. A host that has not wired the resolver yet therefore
    /// behaves exactly as it did before this package, rather than refusing everything.</param>
    /// <param name="objectStore">Defaults to S3 over <paramref name="s3Client"/>. A test seam; the
    /// container never supplies it.</param>
    public FileService(
        IAmazonS3 s3Client,
        EntitlementResolver? entitlements = null,
        OperatorCeilings? operatorCeilings = null,
        IGuildStorageLedger? storageLedger = null,
        IAttachmentObjectStore? objectStore = null,
        TimeProvider? clock = null)
    {
        _objects = objectStore ?? new S3AttachmentObjectStore(s3Client);
        _entitlements = entitlements;
        _operatorCeilings = operatorCeilings ?? OperatorCeilings.None;
        _ledger = storageLedger ?? UnmeteredGuildStorageLedger.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Stores what fits and reports what did not.</summary>
    public async Task<FileUploadResult> UploadFileAsync(
        ICollection<IFormFile> files,
        StorageUploadContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0) return FileUploadResult.Nothing;

        var limits = await ResolveLimitsAsync(context, cancellationToken);

        var used = limits.QuotaBytes == EntitlementValue.Unlimited || context.GuildId is null
            ? 0
            : await _ledger.GetUsedBytesAsync(context.GuildId, cancellationToken);

        var accepted = new List<IFormFile>(files.Count);
        var rejected = new List<RejectedUpload>();
        long plannedBytes = 0;

        foreach (var file in files)
        {
            if (file.Length > limits.UploadCeilingBytes)
            {
                rejected.Add(TooLarge(file, limits));
                continue;
            }

            if (limits.QuotaBytes != EntitlementValue.Unlimited
                && used + plannedBytes + file.Length > limits.QuotaBytes)
            {
                rejected.Add(OverQuota(file, limits, used + plannedBytes));
                continue;
            }

            accepted.Add(file);
            plannedBytes += file.Length;
        }

        var uploaded = await StoreAsync(accepted, context, cancellationToken);

        return new FileUploadResult(uploaded, rejected);
    }

    /// <summary>A file past the single-upload ceiling.</summary>
    private static RejectedUpload TooLarge(IFormFile file, StorageLimits limits) =>
        new(file.FileName,
            file.Length,
            StorageRefusalCode.UploadTooLarge,
            limits.UploadKey,
            limits.UploadCeilingBytes,
            limits.UploadCause,
            limits.UploadBoundBy,
            Degradation(limits.UploadKey, file.Length, limits.UploadCeilingBytes, limits.UploadCause,
                $"'{file.FileName}' is {file.Length} bytes"));

    /// <summary>A file the guild has no room for.</summary>
    private static RejectedUpload OverQuota(IFormFile file, StorageLimits limits, long alreadyHeld) =>
        new(file.FileName,
            file.Length,
            StorageRefusalCode.GuildQuotaExhausted,
            EntitlementKeys.StorageGuildQuotaBytes,
            limits.QuotaBytes,
            limits.QuotaCause,
            limits.QuotaBoundBy,
            Degradation(EntitlementKeys.StorageGuildQuotaBytes, alreadyHeld + file.Length, limits.QuotaBytes,
                limits.QuotaCause, $"'{file.FileName}' would take the guild to {alreadyHeld + file.Length} bytes"));

    private static EntitlementDegradation Degradation(
        EntitlementKey key, long requested, long granted, EntitlementDegradationReason reason, string detail) =>
        new(key.Name,
            key.Format(EntitlementValue.OfNumber(requested)),
            key.Format(EntitlementValue.OfNumber(granted)),
            reason,
            detail);

    /// <summary>Gives quota back for bytes that are no longer stored.</summary>
    public Task<long> ReleaseAsync(string guildId, long sizeBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

        return _ledger.AddAsync(guildId, -sizeBytes, cancellationToken);
    }

    /// <summary>What a guild is currently counted as storing, for the settings screen that section
    /// 2.5 of the downgrade policy promises will keep showing what is over a limit.</summary>
    public Task<long> UsedBytesAsync(string guildId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        return _ledger.GetUsedBytesAsync(guildId, cancellationToken);
    }

    /// <summary>A guild's consumption in the shape the client's meter reads.</summary>
    public async Task<EntitlementUsageDto> UsageForGuildAsync(
        string guildId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);

        var used = await _ledger.GetUsedBytesAsync(guildId, cancellationToken);

        return new EntitlementUsageDto(
            EntitlementSubjectDto.From(EntitlementSubject.ForGuild(guildId)),
            _clock.GetUtcNow(),
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [EntitlementKeys.StorageGuildQuotaBytes.Name] = used,
            });
    }

    /// <summary>The effective limits for a context, without uploading anything.</summary>
    public async Task<StorageLimits> LimitsForAsync(
        StorageUploadContext context, CancellationToken cancellationToken = default) =>
        await ResolveLimitsAsync(context, cancellationToken);

    private async Task<IReadOnlyList<UploadedFile>> StoreAsync(
        IReadOnlyList<IFormFile> accepted, StorageUploadContext context, CancellationToken cancellationToken)
    {
        var bucketName = Env.StorageConfiguration.BucketName;
        var publicUrlBase = Env.StorageConfiguration.PublicUrl;
        var uploaded = new List<UploadedFile>(accepted.Count);

        foreach (var file in accepted)
        {
            var id = Attachment.GenerateId();

            await using (var stream = file.OpenReadStream())
            {
                await _objects.PutAsync(bucketName, id, file.ContentType, stream, cancellationToken);
            }

            if (context.GuildId is not null)
            {
                await _ledger.AddAsync(context.GuildId, file.Length, cancellationToken);
            }

            uploaded.Add(new UploadedFile
            {
                Id = id,
                Url = $"{publicUrlBase}/{bucketName}/{id}",
                FileName = file.FileName,
                SizeBytes = file.Length,
                ContentType = file.ContentType,
            });
        }

        return uploaded;
    }

    private async Task<StorageLimits> ResolveLimitsAsync(
        StorageUploadContext context, CancellationToken cancellationToken)
    {
        var quotaKey = EntitlementKeys.StorageGuildQuotaBytes;

        if (context.GuildId is null)
        {
            var userKey = EntitlementKeys.UserUploadMaxBytes;
            var userOnly = _entitlements is null || context.UserId is null
                ? EntitlementSet.Empty
                : await _entitlements.ResolveAsync(
                    EntitlementSubject.ForUser(context.UserId), cancellationToken);

            var userCeiling = Ceiling(
                userKey, userOnly.Value(userKey),
                EntitlementDegradationReason.UserPlanLimit, EntitlementBoundBy.User);

            return new StorageLimits(userKey, userCeiling, NoQuota);
        }

        var uploadKey = EntitlementKeys.StorageUploadMaxBytes;

        if (_entitlements is null || context.UserId is null)
        {
            return new StorageLimits(
                uploadKey,
                Ceiling(uploadKey, uploadKey.Default,
                    EntitlementDegradationReason.GuildPlanLimit, EntitlementBoundBy.Guild),
                Quota(quotaKey, quotaKey.Default));
        }

        var guild = EntitlementSubject.ForGuild(context.GuildId);
        var user = EntitlementSubject.ForUser(context.UserId);

        // The value comes from the resolver's own pairing and is not recomputed here.
        var effective = await _entitlements.ResolveEffectiveAsync(guild, user, cancellationToken);
        var guildSide = await _entitlements.ResolveAsync(guild, cancellationToken).ConfigureAwait(false);
        var userSide = await _entitlements.ResolveAsync(user, cancellationToken).ConfigureAwait(false);

        var guildBytes = guildSide.Value(uploadKey).AsNumber;
        var userBytes = userSide.Value(uploadKey).AsNumber;

        var reason = guildBytes == userBytes
            ? EntitlementDegradationReason.GuildPlanLimit
            : EntitlementDegradationReason.PairedCeiling;

        var side = guildBytes <= userBytes ? EntitlementBoundBy.Guild : EntitlementBoundBy.User;

        return new StorageLimits(
            uploadKey,
            Ceiling(uploadKey, effective.Value(uploadKey), reason, side),
            Quota(quotaKey, effective.Value(quotaKey)));
    }

    /// <summary>The entitlement, clamped by the operator's ceiling, then floored by this instance's
    /// own default when neither had anything to say. An operator ceiling that bound carries no side,
    /// because it is not a subject anybody can upgrade.</summary>
    private StorageLimit Ceiling(
        EntitlementKey key, EntitlementValue entitled, EntitlementDegradationReason planReason, string planBoundBy)
    {
        var clamped = _operatorCeilings.Clamp(key, entitled).AsNumber;

        if (clamped == EntitlementValue.Unlimited)
        {
            return new StorageLimit(
                InstanceDefaultUploadCeilingBytes, EntitlementDegradationReason.OperatorCeiling, null);
        }

        return _operatorCeilings.Binds(key, entitled)
            ? new StorageLimit(clamped, EntitlementDegradationReason.OperatorCeiling, null)
            : new StorageLimit(clamped, planReason, planBoundBy);
    }

    /// <summary>The quota, clamped the same way.</summary>
    private StorageLimit Quota(EntitlementKey key, EntitlementValue entitled) =>
        _operatorCeilings.Binds(key, entitled)
            ? new StorageLimit(
                _operatorCeilings.Clamp(key, entitled).AsNumber,
                EntitlementDegradationReason.OperatorCeiling, null)
            : new StorageLimit(
                entitled.AsNumber, EntitlementDegradationReason.GuildPlanLimit, EntitlementBoundBy.Guild);

    /// <summary>An upload with no guild consumes no guild's quota, so there is nothing for the quota
    /// to bind on.</summary>
    private static readonly StorageLimit NoQuota =
        new(EntitlementValue.Unlimited, EntitlementDegradationReason.GuildPlanLimit, EntitlementBoundBy.Guild);
}

/// <summary>One resolved limit: the number, which side bound, and which subject that side is.</summary>
/// <param name="Bytes"><see cref="EntitlementValue.Unlimited"/> when nothing applies.</param>
/// <param name="BoundBy">Null exactly when <paramref name="Reason"/> is an operator ceiling.</param>
public readonly record struct StorageLimit(
    long Bytes, EntitlementDegradationReason Reason, string? BoundBy);

/// <summary>What a given context may actually do, after entitlements, pairing, the operator's
/// ceiling and the instance default have all had their say.</summary>
/// <param name="UploadKey">Which of the two upload keys applied. Naming it matters: the client shows
/// a different upgrade surface for the paired guild key than for the user-only one.</param>
/// <param name="Upload">The largest single file this context may store.</param>
/// <param name="Quota">What the guild may hold in total.</param>
public sealed record StorageLimits(EntitlementKey UploadKey, StorageLimit Upload, StorageLimit Quota)
{
    public long UploadCeilingBytes => Upload.Bytes;

    public EntitlementDegradationReason UploadCause => Upload.Reason;

    public string? UploadBoundBy => Upload.BoundBy;

    public long QuotaBytes => Quota.Bytes;

    public EntitlementDegradationReason QuotaCause => Quota.Reason;

    public string? QuotaBoundBy => Quota.BoundBy;
}
