using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Infrastructure.Seed;

public enum SeedOutcome
{
    /// <summary>The stored seed version already matches the artifact. Nothing was read or written.</summary>
    AlreadyCurrent,

    /// <summary>Another pod holds the lock and is doing the work. Not an error.</summary>
    SkippedLockHeld,

    Applied,
}

/// <summary>Loads the bootstrap game catalog into the database, idempotently.</summary>
public sealed class GameCatalogSeeder(MicroserviceContext ctx, ILogger<GameCatalogSeeder> logger)
{
    /// <summary>Advisory-lock key.</summary>
    private const long AdvisoryLockKey = 0x47414D454341544CL;

    public async Task<SeedOutcome> SeedAsync(CancellationToken ct = default)
    {
        var seed = GameCatalogSeedReader.Read();

        // Fast path, taken on every restart after the first: one primary-key lookup instead of
        // reading a 3.7 MiB artifact into a 35,000-row diff.
        if (await IsCurrentAsync(seed.Version, ct)) return SeedOutcome.AlreadyCurrent;

        var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
        var opened = false;

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            // try_ rather than the blocking form: if another pod is mid-seed there is nothing
            // useful to wait for, and holding a startup path open behind it only turns one slow
            // rollout into a synchronised one.
            await using (var lockCmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
            {
                lockCmd.Parameters.AddWithValue("key", AdvisoryLockKey);
                var acquired = (bool)(await lockCmd.ExecuteScalarAsync(ct))!;

                if (!acquired)
                {
                    logger.LogInformation("Game catalog seed skipped: advisory lock held by another instance.");
                    return SeedOutcome.SkippedLockHeld;
                }
            }

            try
            {
                if (await IsCurrentAsync(seed.Version, ct)) return SeedOutcome.AlreadyCurrent;

                await ApplyAsync(seed, ct);
                return SeedOutcome.Applied;
            }
            finally
            {
                await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                unlockCmd.Parameters.AddWithValue("key", AdvisoryLockKey);
                await unlockCmd.ExecuteScalarAsync(ct);
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    internal async Task<bool> IsCurrentAsync(string version, CancellationToken ct)
    {
        var state = await ctx.GameCatalogStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == GameCatalogState.SingletonId, ct);

        return state is not null && state.SeedVersion == version;
    }

    /// <summary>The reconciliation itself, without the advisory lock.</summary>
    internal async Task ApplyAsync(GameCatalogSeed seed, CancellationToken ct)
    {
        // 35,000 entities through the change tracker with auto-detect on is minutes of
        // DetectChanges scans, not seconds of inserts.
        var previousAutoDetect = ctx.ChangeTracker.AutoDetectChangesEnabled;
        ctx.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            var existing = await ctx.GameApplications
                .Include(g => g.Executables)
                .Where(g => g.DiscordApplicationId != null)
                .ToDictionaryAsync(g => g.DiscordApplicationId!, ct);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int inserted = 0, updated = 0, preserved = 0, skippedExecutables = 0;

            foreach (var app in seed.Apps)
            {
                if (string.IsNullOrWhiteSpace(app.DiscordApplicationId) || string.IsNullOrWhiteSpace(app.Name))
                    continue;

                seen.Add(app.DiscordApplicationId);

                var steamAppId = app.Stores is not null
                                 && app.Stores.TryGetValue("steam", out var steamIds)
                                 && steamIds.Length > 0
                    ? steamIds[0]
                    : null;

                if (existing.TryGetValue(app.DiscordApplicationId, out var row))
                {
                    // A human or a community submission has spoken for this application.
                    if (row.Source != GameCatalogSource.Seeded)
                    {
                        preserved++;
                        continue;
                    }

                    row.Name = app.Name;
                    row.Aliases = app.Aliases ?? [];
                    row.SteamAppId = steamAppId;

                    // Replace wholesale rather than diffing: executable rules are small, unkeyed
                    // by anything stable upstream, and a rule that was *removed* upstream is
                    // exactly the case a diff-by-name would silently keep forever.
                    ctx.GameExecutables.RemoveRange(row.Executables);
                    row.Executables.Clear();
                    AddExecutables(row, app, ref skippedExecutables);

                    updated++;
                }
                else
                {
                    var created = new GameApplication
                    {
                        Id = GameApplication.GenerateId(),
                        DiscordApplicationId = app.DiscordApplicationId,
                        Name = app.Name,
                        Aliases = app.Aliases ?? [],
                        SteamAppId = steamAppId,
                        Source = GameCatalogSource.Seeded,
                        IsEnabled = true,
                    };

                    AddExecutables(created, app, ref skippedExecutables);
                    ctx.GameApplications.Add(created);
                    inserted++;
                }
            }

            // Applications dropped from the artifact between seed versions. Only ours to remove.
            var stale = existing
                .Where(kv => kv.Value.Source == GameCatalogSource.Seeded && !seen.Contains(kv.Key))
                .Select(kv => kv.Value)
                .ToList();

            if (stale.Count > 0) ctx.GameApplications.RemoveRange(stale);

            var state = await ctx.GameCatalogStates.FirstOrDefaultAsync(s => s.Id == GameCatalogState.SingletonId, ct);
            if (state is null)
            {
                ctx.GameCatalogStates.Add(new GameCatalogState
                {
                    Id = GameCatalogState.SingletonId,
                    SeedVersion = seed.Version,
                    SeededAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                state.SeedVersion = seed.Version;
                state.SeededAt = DateTimeOffset.UtcNow;
            }

            // Not a Wolverine handler - this runs from a hosted service, so there is no
            // transactional middleware to commit on our behalf.
            ctx.ChangeTracker.DetectChanges();
            await ctx.SaveChangesAsync(ct);

            logger.LogInformation(
                "Game catalog seed {Version} applied: {Inserted} inserted, {Updated} updated, {Removed} removed, "
                + "{Preserved} community/manual rows preserved, {SkippedExecutables} executable rules skipped.",
                seed.Version, inserted, updated, stale.Count, preserved, skippedExecutables);
        }
        finally
        {
            ctx.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
        }
    }

    private static void AddExecutables(GameApplication target, SeedApp app, ref int skipped)
    {
        foreach (var exe in app.Executables ?? [])
        {
            if (string.IsNullOrWhiteSpace(exe.Name)) continue;

            if (!TryParsePlatform(exe.Os, out var platform))
            {
                // An OS value we do not model.
                skipped++;
                continue;
            }

            var name = exe.Name;
            var slash = name.LastIndexOf('/');

            target.Executables.Add(new GameExecutable
            {
                Id = GameExecutable.GenerateId(),
                GameApplicationId = target.Id,
                Name = name,
                Basename = slash >= 0 ? name[(slash + 1)..] : name,
                Os = platform,
                IsLauncher = exe.IsLauncher,
                IsNegated = exe.Negated,
            });
        }
    }

    private static bool TryParsePlatform(string os, out GamePlatform platform)
    {
        switch (os?.Trim().ToLowerInvariant())
        {
            case "win32": platform = GamePlatform.Win32; return true;
            case "darwin": platform = GamePlatform.Darwin; return true;
            case "linux": platform = GamePlatform.Linux; return true;
            default: platform = default; return false;
        }
    }
}
