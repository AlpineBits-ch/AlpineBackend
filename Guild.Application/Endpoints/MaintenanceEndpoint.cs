using System.Globalization;
using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// The house's equipment, on a <see cref="ChannelType.Maintenance"/> channel: what there is, when
/// it was last looked at, who to call, and what has broken.
/// </summary>
[Authorize]
public class MaintenanceEndpoint
{
    private const int MaxNameLength = 100;
    private const int MaxTitleLength = 200;
    private const int MaxShortTextLength = 100;
    private const int MaxNotesLength = 2000;

    /// <summary>Ten years.</summary>
    private const int MaxServiceIntervalDays = 3650;

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    /// <summary>The horizon the attention board treats a warranty as urgent within, matching the
    /// sweep's warning window so the board and the notification never disagree.</summary>
    private const int WarrantyHorizonDays = MaintenanceAsset.WarrantyWarningDays;

    private static MaintenanceAssetDto ToDto(MaintenanceAsset asset, DateTimeOffset now) => new()
    {
        Id = asset.Id,
        ChannelId = asset.ChannelId,
        Name = asset.Name,
        Location = asset.Location,
        Brand = asset.Brand,
        Model = asset.Model,
        SerialNumber = asset.SerialNumber,
        PurchasedAt = asset.PurchasedAt,
        WarrantyUntil = asset.WarrantyUntil,
        VendorName = asset.VendorName,
        VendorPhone = asset.VendorPhone,
        VendorEmail = asset.VendorEmail,
        Notes = asset.Notes,
        ServiceIntervalDays = asset.ServiceIntervalDays,
        LastServicedAt = asset.LastServicedAt,
        NextServiceAt = asset.NextServiceAt,
        Status = asset.Status,
        StatusNote = asset.StatusNote,
        IsServiceOverdue = asset.IsServiceOverdue(now),
        IsWarrantyExpiring = asset.IsWarrantyExpiring(now),
        AddedByUserId = asset.AddedByUserId,
    };

    private static MaintenanceRecordDto ToDto(MaintenanceRecord record) => new()
    {
        Id = record.Id,
        AssetId = record.AssetId,
        ChannelId = record.ChannelId,
        Title = record.Title,
        Description = record.Description,
        PerformedAt = record.PerformedAt,
        PerformedByUserId = record.PerformedByUserId,
        VendorName = record.VendorName,
        CostMinor = record.CostMinor,
        Currency = record.Currency,
        ExpenseId = record.ExpenseId,
    };

    // ══════════════════════════════════════════════════════════════════════════ Assets
    // ══════════════════════════════════════════════════════════════════════════

    [WolverineGet("/api/v1/channels/{channelId}/maintenance-assets")]
    public async Task<IResult> ListAssetsAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Maintenance, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var assets = await ctx.Set<MaintenanceAsset>().AsNoTracking()
            .Where(a => a.ChannelId == channelId)
            .OrderBy(a => a.Name)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        return Results.Ok(assets.Select(a => ToDto(a, now)));
    }

    [WolverineGet("/api/v1/maintenance-assets/{assetId}")]
    public async Task<IResult> GetAssetAsync(string assetId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var asset = await ctx.Set<MaintenanceAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId);
        if (asset is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            asset.ChannelId, ChannelType.Maintenance, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        return Results.Ok(ToDto(asset, DateTimeOffset.UtcNow));
    }

    [WolverinePost("/api/v1/channels/{channelId}/maintenance-assets")]
    public async Task<IResult> CreateAssetAsync(string channelId, CreateMaintenanceAssetDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(
            channelId, ChannelType.Maintenance, userId, Permissions.ManageMaintenance);
        if (access.ToFailure() is { } failure) return failure;

        if (ValidateAssetText(dto.Name, dto.Location, dto.Brand, dto.Model, dto.SerialNumber,
                dto.VendorName, dto.VendorPhone, dto.VendorEmail, dto.Notes) is { } textError)
            return Results.BadRequest(textError);

        if (dto.ServiceIntervalDays is { } interval && interval is < 1 or > MaxServiceIntervalDays)
            return Results.BadRequest($"ServiceIntervalDays must be between 1 and {MaxServiceIntervalDays}");

        var asset = MaintenanceAsset.Create(new CreateMaintenanceAssetParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            Name = dto.Name.Trim(),
            Location = Trimmed(dto.Location),
            Brand = Trimmed(dto.Brand),
            Model = Trimmed(dto.Model),
            SerialNumber = Trimmed(dto.SerialNumber),
            PurchasedAt = dto.PurchasedAt,
            WarrantyUntil = dto.WarrantyUntil,
            VendorName = Trimmed(dto.VendorName),
            VendorPhone = Trimmed(dto.VendorPhone),
            VendorEmail = Trimmed(dto.VendorEmail),
            Notes = Trimmed(dto.Notes),
            ServiceIntervalDays = dto.ServiceIntervalDays,
            LastServicedAt = dto.LastServicedAt,
            AddedByUserId = userId,
        });

        ctx.Add(asset);

        auditLog.Log(asset.GuildId, userId, AuditActionType.MaintenanceAssetCreated, asset.Id,
            new { asset.Name, asset.Location, asset.ServiceIntervalDays, asset.WarrantyUntil });

        await ctx.SaveChangesAsync();

        var dtoOut = ToDto(asset, DateTimeOffset.UtcNow);

        await household.BroadcastAsync(asset.GuildId, channelId, "guild.MaintenanceAssetCreated",
            new { GuildId = asset.GuildId, ChannelId = channelId, Asset = dtoOut });

        return Results.Ok(dtoOut);
    }

    [WolverinePatch("/api/v1/maintenance-assets/{assetId}")]
    public async Task<IResult> UpdateAssetAsync(string assetId, UpdateMaintenanceAssetDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var asset = await ctx.Set<MaintenanceAsset>().FirstOrDefaultAsync(a => a.Id == assetId);
        if (asset is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            asset.ChannelId, ChannelType.Maintenance, userId, Permissions.ManageMaintenance);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Name is not null && string.IsNullOrWhiteSpace(dto.Name))
            return Results.BadRequest("Name cannot be empty");

        if (ValidateAssetText(dto.Name, dto.Location, dto.Brand, dto.Model, dto.SerialNumber,
                dto.VendorName, dto.VendorPhone, dto.VendorEmail, dto.Notes) is { } textError)
            return Results.BadRequest(textError);

        if (dto.ServiceIntervalDays is { } interval && interval is < 1 or > MaxServiceIntervalDays)
            return Results.BadRequest($"ServiceIntervalDays must be between 1 and {MaxServiceIntervalDays}");

        if (dto.Name is not null) asset.Name = dto.Name.Trim();
        if (dto.Location is not null) asset.Location = Trimmed(dto.Location);
        if (dto.Brand is not null) asset.Brand = Trimmed(dto.Brand);
        if (dto.Model is not null) asset.Model = Trimmed(dto.Model);
        if (dto.SerialNumber is not null) asset.SerialNumber = Trimmed(dto.SerialNumber);
        if (dto.VendorName is not null) asset.VendorName = Trimmed(dto.VendorName);
        if (dto.VendorPhone is not null) asset.VendorPhone = Trimmed(dto.VendorPhone);
        if (dto.VendorEmail is not null) asset.VendorEmail = Trimmed(dto.VendorEmail);
        if (dto.Notes is not null) asset.Notes = Trimmed(dto.Notes);

        if (dto.ClearPurchasedAt == true) asset.PurchasedAt = null;
        else if (dto.PurchasedAt is not null) asset.PurchasedAt = dto.PurchasedAt;

        // Moving the warranty date makes any previous warning stale, so the stamp is released and
        // the sweep may warn again.
        if (dto.ClearWarrantyUntil == true)
        {
            asset.WarrantyUntil = null;
            asset.WarrantyNotifiedAt = null;
        }
        else if (dto.WarrantyUntil is not null && dto.WarrantyUntil != asset.WarrantyUntil)
        {
            asset.WarrantyUntil = dto.WarrantyUntil;
            asset.WarrantyNotifiedAt = null;
        }

        var scheduleChanged = false;

        if (dto.ClearServiceInterval == true)
        {
            asset.ServiceIntervalDays = null;
            scheduleChanged = true;
        }
        else if (dto.ServiceIntervalDays is not null && dto.ServiceIntervalDays != asset.ServiceIntervalDays)
        {
            asset.ServiceIntervalDays = dto.ServiceIntervalDays;
            scheduleChanged = true;
        }

        if (dto.LastServicedAt is not null && dto.LastServicedAt != asset.LastServicedAt)
        {
            asset.LastServicedAt = dto.LastServicedAt;
            scheduleChanged = true;
        }

        if (scheduleChanged)
        {
            asset.RescheduleService(DateTimeOffset.UtcNow);
            asset.ServiceNotifiedAt = null;
        }

        asset.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(asset.GuildId, userId, AuditActionType.MaintenanceAssetUpdated, asset.Id,
            new { asset.Name, asset.ServiceIntervalDays, asset.NextServiceAt, asset.WarrantyUntil });

        await ctx.SaveChangesAsync();

        var dtoOut = ToDto(asset, DateTimeOffset.UtcNow);

        await household.BroadcastAsync(asset.GuildId, asset.ChannelId, "guild.MaintenanceAssetUpdated",
            new { GuildId = asset.GuildId, ChannelId = asset.ChannelId, Asset = dtoOut });

        return Results.Ok(dtoOut);
    }

    [WolverineDelete("/api/v1/maintenance-assets/{assetId}")]
    public async Task<IResult> DeleteAssetAsync(string assetId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var asset = await ctx.Set<MaintenanceAsset>().FirstOrDefaultAsync(a => a.Id == assetId);
        if (asset is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            asset.ChannelId, ChannelType.Maintenance, userId, Permissions.ManageMaintenance);
        if (access.ToFailure() is { } failure) return failure;

        // Logged before the remove, so the entry still carries what was deleted rather than the id
        // of a row nobody can look up any more.
        auditLog.Log(asset.GuildId, userId, AuditActionType.MaintenanceAssetDeleted, asset.Id,
            new { asset.Name, asset.Location, asset.SerialNumber });

        // The records survive: the history of what was done to a machine is the reason the log
        // exists, and it outlives the machine. The FK is SetNull, not Cascade.
        ctx.Remove(asset);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(asset.GuildId, asset.ChannelId, "guild.MaintenanceAssetDeleted",
            new { GuildId = asset.GuildId, ChannelId = asset.ChannelId, AssetId = assetId });

        return Results.NoContent();
    }

    /// <summary>Changes what state an asset is in.</summary>
    [WolverinePut("/api/v1/maintenance-assets/{assetId}/status")]
    public async Task<IResult> SetStatusAsync(string assetId, UpdateAssetStatusDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MaintenanceAlertService alerts,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var asset = await ctx.Set<MaintenanceAsset>().FirstOrDefaultAsync(a => a.Id == assetId);
        if (asset is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            asset.ChannelId, ChannelType.Maintenance, userId, Permissions.LogMaintenance);
        if (access.ToFailure() is { } failure) return failure;

        if (!Enum.IsDefined(dto.Status)) return Results.BadRequest("Unknown status");
        if (dto.Note is { Length: > MaxNotesLength })
            return Results.BadRequest($"Note must be {MaxNotesLength} characters or fewer");

        // The transition, not the value.
        var becameBroken = dto.Status == AssetStatus.Broken && asset.Status != AssetStatus.Broken;

        asset.Status = dto.Status;
        asset.StatusNote = Trimmed(dto.Note);
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(asset.GuildId, userId, AuditActionType.MaintenanceAssetUpdated, asset.Id,
            new { asset.Status, asset.StatusNote });

        await ctx.SaveChangesAsync();

        var dtoOut = ToDto(asset, DateTimeOffset.UtcNow);

        await household.BroadcastAsync(asset.GuildId, asset.ChannelId, "guild.MaintenanceAssetUpdated",
            new { GuildId = asset.GuildId, ChannelId = asset.ChannelId, Asset = dtoOut });

        if (becameBroken) await alerts.BrokenAsync(asset, userId);

        return Results.Ok(dtoOut);
    }

    /// <summary>Marks an asset serviced and writes the log entry for it in one call.</summary>
    [WolverinePost("/api/v1/maintenance-assets/{assetId}/serviced")]
    public async Task<IResult> RecordServicedAsync(string assetId, RecordServiceDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var asset = await ctx.Set<MaintenanceAsset>().FirstOrDefaultAsync(a => a.Id == assetId);
        if (asset is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            asset.ChannelId, ChannelType.Maintenance, userId, Permissions.LogMaintenance);
        if (access.ToFailure() is { } failure) return failure;

        var title = string.IsNullOrWhiteSpace(dto.Title) ? "Serviced" : dto.Title.Trim();
        if (title.Length > MaxTitleLength)
            return Results.BadRequest($"Title must be {MaxTitleLength} characters or fewer");
        if (dto.Notes is { Length: > MaxNotesLength })
            return Results.BadRequest($"Notes must be {MaxNotesLength} characters or fewer");

        if (await ValidateMoneyAsync(ctx, asset.GuildId, dto.CostMinor, dto.Currency, dto.ExpenseId) is { } moneyError)
            return Results.BadRequest(moneyError);

        var performedAt = dto.PerformedAt ?? DateTimeOffset.UtcNow;

        var record = MaintenanceRecord.Create(new CreateMaintenanceRecordParams
        {
            AssetId = asset.Id,
            ChannelId = asset.ChannelId,
            GuildId = asset.GuildId,
            Title = title,
            Description = Trimmed(dto.Notes),
            PerformedAt = performedAt,
            PerformedByUserId = userId,
            VendorName = Trimmed(dto.VendorName) ?? asset.VendorName,
            CostMinor = dto.CostMinor,
            Currency = NormalizedCurrency(dto.Currency),
            ExpenseId = dto.ExpenseId,
        });

        // Deliberately leaves Status alone: a service is not proof the thing works.
        asset.RecordService(performedAt);

        ctx.Add(record);

        auditLog.Log(asset.GuildId, userId, AuditActionType.MaintenanceRecordCreated, record.Id,
            new { record.AssetId, record.Title, record.PerformedAt, record.CostMinor });

        await ctx.SaveChangesAsync();

        var assetDto = ToDto(asset, DateTimeOffset.UtcNow);

        await household.BroadcastAsync(asset.GuildId, asset.ChannelId, "guild.MaintenanceRecordCreated",
            new { GuildId = asset.GuildId, ChannelId = asset.ChannelId, Record = ToDto(record) });

        await household.BroadcastAsync(asset.GuildId, asset.ChannelId, "guild.MaintenanceAssetUpdated",
            new { GuildId = asset.GuildId, ChannelId = asset.ChannelId, Asset = assetDto });

        return Results.Ok(new { Asset = assetDto, Record = ToDto(record) });
    }

    // ══════════════════════════════════════════════════════════════════════════ Records
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The channel's maintenance log, newest first, in pages.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/maintenance-records")]
    public async Task<IResult> ListRecordsAsync(string channelId, string? assetId, int? limit, string? cursor,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Maintenance, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var query = ctx.Set<MaintenanceRecord>().AsNoTracking().Where(r => r.ChannelId == channelId);

        if (!string.IsNullOrWhiteSpace(assetId)) query = query.Where(r => r.AssetId == assetId);

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            // A malformed cursor is a 400 rather than a silent first page: a client that has
            // corrupted its cursor is paging in a circle, and quietly restarting the list is how
            // that becomes an infinite scroll nobody can reproduce.
            if (!TryParseCursor(cursor, out var beforeAt, out var beforeId))
                return Results.BadRequest("Malformed cursor");

            query = query.Where(r => r.PerformedAt < beforeAt
                                     || (r.PerformedAt == beforeAt && string.Compare(r.Id, beforeId) < 0));
        }

        // One extra row is the has-more probe, so the client never makes a second request just to
        // learn the list ended.
        var page = await query
            .OrderByDescending(r => r.PerformedAt)
            .ThenByDescending(r => r.Id)
            .Take(take + 1)
            .ToListAsync();

        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);

        return Results.Ok(new
        {
            Items = page.Select(ToDto).ToList(),
            NextCursor = hasMore && page.Count > 0
                ? $"{page[^1].PerformedAt:O}|{page[^1].Id}"
                : null,
        });
    }

    private static bool TryParseCursor(string cursor, out DateTimeOffset performedAt, out string id)
    {
        performedAt = default;
        id = "";

        var separator = cursor.LastIndexOf('|');
        if (separator <= 0 || separator == cursor.Length - 1) return false;

        if (!DateTimeOffset.TryParse(cursor[..separator], null, DateTimeStyles.RoundtripKind, out performedAt))
            return false;

        id = cursor[(separator + 1)..];
        return true;
    }

    [WolverinePost("/api/v1/channels/{channelId}/maintenance-records")]
    public async Task<IResult> CreateRecordAsync(string channelId, CreateMaintenanceRecordDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Maintenance, userId, Permissions.LogMaintenance);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Title)) return Results.BadRequest("Title is required");
        if (dto.Title.Length > MaxTitleLength)
            return Results.BadRequest($"Title must be {MaxTitleLength} characters or fewer");
        if (dto.Description is { Length: > MaxNotesLength })
            return Results.BadRequest($"Description must be {MaxNotesLength} characters or fewer");

        var guildId = access.Channel!.GuildId;

        // An asset id from another channel would put a repair in a log its readers cannot see the
        // subject of, and would let this endpoint probe for assets in channels the caller has no
        // access to.
        if (!string.IsNullOrWhiteSpace(dto.AssetId) &&
            !await ctx.Set<MaintenanceAsset>().AsNoTracking()
                .AnyAsync(a => a.Id == dto.AssetId && a.ChannelId == channelId))
            return Results.BadRequest("AssetId must be an asset on this channel");

        if (await ValidateMoneyAsync(ctx, guildId, dto.CostMinor, dto.Currency, dto.ExpenseId) is { } moneyError)
            return Results.BadRequest(moneyError);

        var record = MaintenanceRecord.Create(new CreateMaintenanceRecordParams
        {
            AssetId = string.IsNullOrWhiteSpace(dto.AssetId) ? null : dto.AssetId,
            ChannelId = channelId,
            GuildId = guildId,
            Title = dto.Title.Trim(),
            Description = Trimmed(dto.Description),
            PerformedAt = dto.PerformedAt ?? DateTimeOffset.UtcNow,
            PerformedByUserId = userId,
            VendorName = Trimmed(dto.VendorName),
            CostMinor = dto.CostMinor,
            Currency = NormalizedCurrency(dto.Currency),
            ExpenseId = dto.ExpenseId,
        });

        ctx.Add(record);

        auditLog.Log(guildId, userId, AuditActionType.MaintenanceRecordCreated, record.Id,
            new { record.AssetId, record.Title, record.PerformedAt, record.CostMinor });

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(guildId, channelId, "guild.MaintenanceRecordCreated",
            new { GuildId = guildId, ChannelId = channelId, Record = ToDto(record) });

        return Results.Ok(ToDto(record));
    }

    [WolverinePatch("/api/v1/maintenance-records/{recordId}")]
    public async Task<IResult> UpdateRecordAsync(string recordId, UpdateMaintenanceRecordDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var record = await ctx.Set<MaintenanceRecord>().FirstOrDefaultAsync(r => r.Id == recordId);
        if (record is null) return Results.NotFound();

        // Fixing your own entry needs only LogMaintenance; editing somebody else's rewrites what
        // they said happened to the house, so that is a moderator action - the same split
        // LedgerEndpoint makes over an expense.
        var required = record.PerformedByUserId == userId
            ? Permissions.LogMaintenance
            : Permissions.ManageMaintenance;

        var access = await household.ResolveAsync(record.ChannelId, ChannelType.Maintenance, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Title)) return Results.BadRequest("Title cannot be empty");
            if (dto.Title.Length > MaxTitleLength)
                return Results.BadRequest($"Title must be {MaxTitleLength} characters or fewer");
            record.Title = dto.Title.Trim();
        }

        if (dto.Description is { Length: > MaxNotesLength })
            return Results.BadRequest($"Description must be {MaxNotesLength} characters or fewer");

        var expenseId = dto.ClearExpense == true ? null : dto.ExpenseId ?? record.ExpenseId;

        if (await ValidateMoneyAsync(ctx, record.GuildId,
                dto.ClearCost == true ? null : dto.CostMinor,
                dto.Currency, dto.ClearExpense == true ? null : dto.ExpenseId) is { } moneyError)
            return Results.BadRequest(moneyError);

        if (dto.Description is not null) record.Description = Trimmed(dto.Description);
        if (dto.PerformedAt is not null) record.PerformedAt = dto.PerformedAt.Value;
        if (dto.VendorName is not null) record.VendorName = Trimmed(dto.VendorName);

        if (dto.ClearCost == true) record.CostMinor = null;
        else if (dto.CostMinor is not null) record.CostMinor = dto.CostMinor;

        if (dto.Currency is not null) record.Currency = NormalizedCurrency(dto.Currency);

        record.ExpenseId = expenseId;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(record.GuildId, record.ChannelId, "guild.MaintenanceRecordUpdated",
            new { GuildId = record.GuildId, ChannelId = record.ChannelId, Record = ToDto(record) });

        return Results.Ok(ToDto(record));
    }

    [WolverineDelete("/api/v1/maintenance-records/{recordId}")]
    public async Task<IResult> DeleteRecordAsync(string recordId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var record = await ctx.Set<MaintenanceRecord>().FirstOrDefaultAsync(r => r.Id == recordId);
        if (record is null) return Results.NotFound();

        var required = record.PerformedByUserId == userId
            ? Permissions.LogMaintenance
            : Permissions.ManageMaintenance;

        var access = await household.ResolveAsync(record.ChannelId, ChannelType.Maintenance, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        ctx.Remove(record);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(record.GuildId, record.ChannelId, "guild.MaintenanceRecordDeleted",
            new { GuildId = record.GuildId, ChannelId = record.ChannelId, RecordId = recordId });

        return Results.NoContent();
    }

    // ══════════════════════════════════════════════════════════════════════════ The attention
    // board ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Everything in the house that is asking for a human: what is broken, what is overdue a
    /// service, and what is about to lose its warranty.
    /// </summary>
    [WolverineGet("/api/v1/guilds/{guildId}/maintenance/attention")]
    public async Task<IResult> AttentionAsync(string guildId,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Maintenance))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        var warrantyHorizon = now.AddDays(WarrantyHorizonDays);

        var candidates = await ctx.Set<MaintenanceAsset>().AsNoTracking()
            .Where(a => a.GuildId == guildId
                        && (a.Status == AssetStatus.Broken
                            || a.Status == AssetStatus.NeedsAttention
                            || (a.NextServiceAt != null && a.NextServiceAt <= now)
                            || (a.WarrantyUntil != null && a.WarrantyUntil > now && a.WarrantyUntil <= warrantyHorizon)))
            .OrderBy(a => a.Name)
            .ToListAsync();

        if (candidates.Count == 0) return Results.Ok(Array.Empty<MaintenanceAttentionDto>());

        var visibleChannels = await permissionService.FilterChannelsWithPermissionAsync(
            userId, guildId, candidates.Select(a => a.ChannelId).Distinct().ToList(), Permissions.ViewChannel);

        var board = candidates
            .Where(a => visibleChannels.Contains(a.ChannelId))
            .Select(a => new MaintenanceAttentionDto { Asset = ToDto(a, now), Reasons = ReasonsFor(a, now) })
            .ToList();

        return Results.Ok(board);
    }

    /// <summary>Why an asset is on the board.</summary>
    private static List<string> ReasonsFor(MaintenanceAsset asset, DateTimeOffset now)
    {
        var reasons = new List<string>(3);

        if (asset.Status == AssetStatus.Broken) reasons.Add("broken");
        if (asset.Status == AssetStatus.NeedsAttention) reasons.Add("needs_attention");
        if (asset.IsServiceOverdue(now)) reasons.Add("service_overdue");
        if (asset.IsWarrantyExpiring(now)) reasons.Add("warranty_expiring");

        return reasons;
    }

    // ── Shared validation ────────────────────────────────────────────────────

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizedCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();

    private static string? ValidateAssetText(string? name, string? location, string? brand, string? model,
        string? serialNumber, string? vendorName, string? vendorPhone, string? vendorEmail, string? notes)
    {
        if (name is { Length: > MaxNameLength })
            return $"Name must be {MaxNameLength} characters or fewer";

        foreach (var (label, value) in new[]
                 {
                     ("Location", location), ("Brand", brand), ("Model", model),
                     ("SerialNumber", serialNumber), ("VendorName", vendorName),
                     ("VendorPhone", vendorPhone), ("VendorEmail", vendorEmail),
                 })
        {
            if (value is { Length: > MaxShortTextLength })
                return $"{label} must be {MaxShortTextLength} characters or fewer";
        }

        return notes is { Length: > MaxNotesLength }
            ? $"Notes must be {MaxNotesLength} characters or fewer"
            : null;
    }

    /// <summary>
    /// Validates the money side of a record: a non-negative cost, a real ISO code, and an expense
    /// that actually exists in this guild.
    /// </summary>
    private static async Task<string?> ValidateMoneyAsync(
        MicroserviceContext ctx, string guildId, long? costMinor, string? currency, string? expenseId)
    {
        if (costMinor is < 0) return "CostMinor cannot be negative";

        var normalized = NormalizedCurrency(currency);
        if (normalized is not null && (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper)))
            return "Currency must be a three-letter ISO-4217 code";

        if (string.IsNullOrWhiteSpace(expenseId)) return null;

        return await ctx.Expenses.AsNoTracking().AnyAsync(e => e.Id == expenseId && e.GuildId == guildId)
            ? null
            : "ExpenseId must be an expense in this guild";
    }
}
