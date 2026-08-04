using AppEnvironment;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Domain.Repositories;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Messaging.Application.Services.Privacy;

/// <summary>Tuning for <see cref="DmRetentionSweepService"/>.</summary>
public sealed class DmRetentionOptions
{
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How many messages one user may have deleted in a single tick, across all their
    /// conversations. A cap rather than "everything": the first sweep after somebody enables a
    /// 7-day window on years of history would otherwise be one enormous burst of deletes and one
    /// enormous burst of <c>MessageDeleted</c> fan-out. Successive ticks finish the job.</summary>
    public int MaxDeletesPerUserPerTick { get; set; } = 500;

    /// <summary>Rows read per page while scanning a conversation.</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>Pages one conversation may be scanned for per tick.</summary>
    public int MaxPagesPerConversationPerTick { get; set; } = 25;

    /// <summary>Accounts examined per tick.</summary>
    public int MaxUsersPerTick { get; set; } = 500;

    /// <summary>
    /// How many sweep intervals a full rotation may take before it is logged as a warning rather
    /// than as information.
    /// </summary>
    public int RotationLagWarningMultiple { get; set; } = 4;

    /// <summary>Whether the sweep may delete from the Scylla message store.</summary>
    public bool ScyllaDeleteEnabled { get; set; } = Env.Retention.DmScyllaDeleteEnabled;

    public static DmRetentionOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new DmRetentionOptions();
        var section = configuration.GetSection("Messaging:DmRetention");
        if (!section.Exists()) return options;

        if (int.TryParse(section["SweepIntervalSeconds"], out var seconds) && seconds > 0)
            options.SweepInterval = TimeSpan.FromSeconds(seconds);

        if (int.TryParse(section["MaxDeletesPerUserPerTick"], out var deletes) && deletes > 0)
            options.MaxDeletesPerUserPerTick = deletes;

        if (int.TryParse(section["PageSize"], out var pageSize) && pageSize > 0)
            options.PageSize = pageSize;

        if (int.TryParse(section["MaxPagesPerConversationPerTick"], out var pages) && pages > 0)
            options.MaxPagesPerConversationPerTick = pages;

        if (int.TryParse(section["MaxUsersPerTick"], out var users) && users > 0)
            options.MaxUsersPerTick = users;

        if (int.TryParse(section["RotationLagWarningMultiple"], out var multiple) && multiple > 0)
            options.RotationLagWarningMultiple = multiple;

        // Only ever read as an explicit "true"/"false"; an unparseable value leaves the Env default
        // in place rather than being coerced to false, so a typo cannot silently disable a path an
        // operator believes they enabled - it stays at whatever RETENTION_DM_SCYLLA_ENABLED said.
        if (bool.TryParse(section["ScyllaDeleteEnabled"], out var scylla))
            options.ScyllaDeleteEnabled = scylla;

        return options;
    }
}

/// <summary>What one tick did.</summary>
public sealed record DmRetentionSweepResult(
    int UsersExamined,
    int MessagesDeleted,
    bool RotationCompleted,
    bool Skipped)
{
    public static readonly DmRetentionSweepResult SkippedResult = new(0, 0, false, true);
}

/// <summary>
/// T2-22. Deletes each user's own DM messages once they are older than that user's
/// <c>DmRetentionDays</c> window.
/// </summary>
public sealed class DmRetentionSweepService(
    IServiceScopeFactory scopeFactory,
    DmRetentionOptions options,
    ILogger<DmRetentionSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Announced before the first delay, not on the first tick: an instance whose sweep interval
        // is six hours would otherwise take six hours to reveal that it is not sweeping at all.
        await AnnounceBackendAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.SweepInterval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "DM retention sweep failed");
            }
        }
    }

    /// <summary>
    /// Says at startup which message store the sweep resolved and, if it is the gated one, that it
    /// is going to do nothing.
    /// </summary>
    private async Task AnnounceBackendAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

            if (IsScyllaPathDisabled(repo))
            {
                logger.LogWarning(
                    "DM retention (T2-22) is configured and the sweep is running, but the Scylla delete path is "
                    + "DISABLED, so no message will be deleted on this instance. User-set DmRetentionDays windows "
                    + "are being accepted and not enforced. Set RETENTION_DM_SCYLLA_ENABLED=true to enable it, "
                    + "after running the ECHO_TEST_SCYLLA-gated range-delete tests against a node of the version "
                    + "this deployment runs.");
                return;
            }

            logger.LogInformation(
                "DM retention (T2-22) sweeping every {Interval} against {Backend}, up to {MaxUsers} account(s) per tick",
                options.SweepInterval, repo.GetType().Name, options.MaxUsersPerTick);
        }
        catch (Exception e)
        {
            // Never the reason the host fails to start.
            logger.LogError(e, "DM retention startup check failed");
        }

        await Task.CompletedTask;
    }

    /// <summary>True when the resolved store is Scylla and the explicit opt-in has not been given.
    /// The EF/Postgres path is never gated - its range read is ordinary SQL, exercised by the
    /// provider-backed suite.</summary>
    private bool IsScyllaPathDisabled(IMessageRepository repo)
        => repo is ScyllaMessageRepository && !options.ScyllaDeleteEnabled;

    /// <summary>One tick's work.</summary>
    public async Task<DmRetentionSweepResult> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var privacy = scope.ServiceProvider.GetRequiredService<PrivacySettingsCache>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        if (IsScyllaPathDisabled(repo))
        {
            // Re-checked here and not only at startup: SweepAsync is the entry point a test or an
            // operator-triggered run uses, and a gate that only guards the timer loop is not a gate.
            logger.LogWarning(
                "DM retention sweep skipped: the Scylla delete path is disabled (RETENTION_DM_SCYLLA_ENABLED)");
            return DmRetentionSweepResult.SkippedResult;
        }

        var now = DateTimeOffset.UtcNow;
        var cursor = await LoadCursorAsync(ctx, now, ct);

        var take = Math.Max(1, options.MaxUsersPerTick);
        var after = cursor.LastUserId;

        var userIds = await NextUserPage(ctx, after, take).ToListAsync(ct);

        if (userIds.Count == 0 && after.Length == 0)
        {
            // No members at all.
            return new DmRetentionSweepResult(0, 0, false, false);
        }

        WarnIfRotationIsLagging(cursor, now);

        var deleted = 0;

        if (userIds.Count > 0)
        {
            var settings = await privacy.GetAsync(userIds, ct);

            foreach (var userId in userIds)
            {
                if (ct.IsCancellationRequested) break;

                if (!settings.TryGetValue(userId, out var record)) continue;
                if (record.DmRetentionDays is not { } days || days <= 0) continue;

                var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

                try
                {
                    deleted += await SweepUserAsync(ctx, repo, bus, userId, cutoff, ct);
                }
                catch (Exception e)
                {
                    // One user's conversation failing must not abandon the rest of the tick - nor
                    // hold the cursor back on them, which would stall everybody behind them.
                    logger.LogError(e, "DM retention sweep failed for {UserId}", userId);
                }
            }
        }

        cursor.UsersSeenThisRotation += userIds.Count;

        // A short page means the scan reached the tail: the rotation is finished now rather than on
        // the next tick's empty read, so a small deployment completes a rotation every tick instead
        // of every other one. A full page leaves the position where it landed.
        var rotationCompleted = userIds.Count < take;
        if (rotationCompleted) CompleteRotation(cursor, now);
        else cursor.LastUserId = userIds[^1];

        cursor.UpdatedAt = now;
        await ctx.SaveChangesAsync(ct);

        return new DmRetentionSweepResult(userIds.Count, deleted, rotationCompleted, false);
    }

    /// <summary>
    /// The accounts one tick examines: distinct member ids ordered after <paramref name="after"/>.
    /// </summary>
    public static IQueryable<string> NextUserPage(MicroserviceContext ctx, string after, int take) =>
        ctx.Members
            .Where(m => string.Compare(m.UserId, after) > 0)
            .Select(m => m.UserId)
            .Distinct()
            .OrderBy(id => id)
            .Take(take);

    /// <summary>Reads the one cursor row, creating it on first ever run.</summary>
    private static async Task<DmRetentionCursor> LoadCursorAsync(
        MicroserviceContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var cursor = await ctx.DmRetentionCursors
            .FirstOrDefaultAsync(c => c.Id == DmRetentionCursor.SingletonId, ct);

        if (cursor is not null) return cursor;

        cursor = new DmRetentionCursor
        {
            Id = DmRetentionCursor.SingletonId,
            LastUserId = string.Empty,
            RotationStartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ctx.DmRetentionCursors.Add(cursor);
        return cursor;
    }

    private void CompleteRotation(DmRetentionCursor cursor, DateTimeOffset now)
    {
        var duration = now - cursor.RotationStartedAt;
        var budget = options.SweepInterval * Math.Max(1, options.RotationLagWarningMultiple);

        cursor.RotationsCompleted++;

        if (duration > budget)
        {
            logger.LogWarning(
                "DM retention completed rotation {Rotation} over {Users} account(s) in {Duration}, longer than "
                + "{Multiple}x the {Interval} sweep interval - retention windows are being honoured late. "
                + "Raise Messaging:DmRetention:MaxUsersPerTick or shorten the interval.",
                cursor.RotationsCompleted, cursor.UsersSeenThisRotation, duration,
                options.RotationLagWarningMultiple, options.SweepInterval);
        }
        else
        {
            logger.LogInformation(
                "DM retention completed rotation {Rotation} over {Users} account(s) in {Duration}",
                cursor.RotationsCompleted, cursor.UsersSeenThisRotation, duration);
        }

        cursor.LastUserId = string.Empty;
        cursor.UsersSeenThisRotation = 0;
        cursor.RotationStartedAt = now;
        cursor.LagWarningIssued = false;
    }

    /// <summary>A rotation that never finishes never logs a completion, so the lag has to be
    /// reported from inside it too. Once per rotation - a six-hourly job that warns on every tick is
    /// a job whose warnings get filtered.</summary>
    private void WarnIfRotationIsLagging(DmRetentionCursor cursor, DateTimeOffset now)
    {
        if (cursor.LagWarningIssued) return;

        var budget = options.SweepInterval * Math.Max(1, options.RotationLagWarningMultiple);
        var elapsed = now - cursor.RotationStartedAt;
        if (elapsed <= budget) return;

        cursor.LagWarningIssued = true;
        logger.LogWarning(
            "DM retention rotation {Rotation} has been running {Elapsed} - longer than {Multiple}x the {Interval} "
            + "sweep interval - and has examined {Users} account(s) so far, currently past {Cursor}. Retention "
            + "windows are being honoured late for everyone it has not reached.",
            cursor.RotationsCompleted + 1, elapsed, options.RotationLagWarningMultiple, options.SweepInterval,
            cursor.UsersSeenThisRotation, cursor.LastUserId);
    }

    private async Task<int> SweepUserAsync(
        MicroserviceContext ctx, IMessageRepository repo, IMessageBus bus,
        string userId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var conversationIds = await ctx.Members
            .Where(m => m.UserId == userId)
            .Select(m => m.ConversationId)
            .Distinct()
            .ToListAsync(ct);

        var budget = options.MaxDeletesPerUserPerTick;
        var deleted = 0;

        foreach (var conversationId in conversationIds)
        {
            if (budget <= 0 || ct.IsCancellationRequested) return deleted;

            // Cursor starts before every possible row.
            var afterCreatedAt = DateTimeOffset.MinValue;
            var afterMessageId = string.Empty;

            for (var page = 0; page < options.MaxPagesPerConversationPerTick && budget > 0; page++)
            {
                var rows = await repo.GetContextMessagesOlderThanAsync(
                    conversationId, cutoff, afterCreatedAt, afterMessageId, options.PageSize);

                if (rows.Count == 0) break;

                var last = rows[^1];
                afterCreatedAt = last.CreatedAt;
                afterMessageId = last.Id;

                var mine = rows
                    .Where(m => string.Equals(m.AuthorId, userId, StringComparison.Ordinal))
                    .Take(budget)
                    .ToList();

                if (mine.Count > 0)
                {
                    await DeleteAsync(ctx, repo, bus, mine, ct);
                    budget -= mine.Count;
                    deleted += mine.Count;

                    logger.LogInformation(
                        "DM retention removed {Count} message(s) authored by {UserId} from {ConversationId} older than {Cutoff:o}",
                        mine.Count, userId, conversationId, cutoff);
                }

                if (rows.Count < options.PageSize) break;
            }
        }

        return deleted;
    }

    private static async Task DeleteAsync(
        MicroserviceContext ctx, IMessageRepository repo, IMessageBus bus,
        IReadOnlyCollection<Message> messages, CancellationToken ct)
    {
        await repo.DeleteMessagesAsync(messages.ToList());

        // The EF-backed repository only stages the removal - every other caller of it is a
        // Wolverine handler whose middleware commits on return, and this loop has no such
        // middleware.
        await ctx.SaveChangesAsync(ct);

        foreach (var message in messages)
        {
            await bus.PublishAsync(new MessageDeleted
            {
                MessageId = message.Id,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                AuthorId = message.AuthorId,
            });
        }
    }
}
