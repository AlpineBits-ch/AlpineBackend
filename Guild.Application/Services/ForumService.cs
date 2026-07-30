using System.Text;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Outcome of a tag-set write.</summary>
public record SetTagsResult(List<string>? TagIds, string? Error = null, bool Forbidden = false)
{
    public bool Succeeded => Error is null && !Forbidden;

    public static SetTagsResult Ok(List<string> tagIds) => new(tagIds);
    public static SetTagsResult Invalid(string error) => new(null, error);
    public static SetTagsResult Denied(string error) => new(null, error, Forbidden: true);
}

/// <summary>Forum behaviour shared by the three endpoints that touch it - tag CRUD, post listing,
/// and post creation. Everything here operates on the change tracker without committing; the
/// Wolverine EF middleware commits at the end of the request, per this service's convention.</summary>
public class ForumService(MicroserviceContext ctx)
{
    /// <summary>The forum's stored config, or an unpersisted default if nobody has configured it.
    /// Read paths never insert - only a PATCH does - so a forum that has never been configured
    /// still answers GET with the values it actually behaves as.</summary>
    public async Task<ForumConfig> GetConfigAsync(string channelId, string guildId)
    {
        var config = await ctx.ForumConfigs.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        return config ?? ForumConfig.Default(channelId, guildId);
    }

    /// <summary>Applied tag ids per post, ordered by tag Position, for a page of posts.</summary>
    public async Task<Dictionary<string, List<string>>> GetTagIdsForPostsAsync(IReadOnlyCollection<string> postIds)
    {
        if (postIds.Count == 0) return [];

        var rows = await ctx.ForumPostTags
            .AsNoTracking()
            .Where(pt => postIds.Contains(pt.ThreadChannelId))
            .Join(ctx.ForumTags.AsNoTracking(), pt => pt.TagId, t => t.Id,
                (pt, t) => new { pt.ThreadChannelId, t.Id, t.Position })
            .ToListAsync();

        return rows
            .GroupBy(r => r.ThreadChannelId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToList());
    }

    /// <summary>Non-archived post counts per tag, for the filter bar's badges.</summary>
    public async Task<Dictionary<string, int>> GetPostCountsAsync(IReadOnlyCollection<string> tagIds)
    {
        if (tagIds.Count == 0) return [];

        var rows = await ctx.ForumPostTags
            .AsNoTracking()
            .Where(pt => tagIds.Contains(pt.TagId))
            .Join(ctx.Channels.AsNoTracking().Where(c => !c.IsArchived), pt => pt.ThreadChannelId, c => c.Id,
                (pt, c) => pt.TagId)
            .GroupBy(tagId => tagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToListAsync();

        return rows.ToDictionary(r => r.TagId, r => r.Count);
    }

    /// <summary>Replaces a post's tag set.</summary>
    public async Task<SetTagsResult> SetPostTagsAsync(
        string postId,
        string forumChannelId,
        IReadOnlyList<string> requestedTagIds,
        bool callerIsModerator,
        bool requireTag)
    {
        var requested = requestedTagIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();

        if (requireTag && requested.Count == 0)
            return SetTagsResult.Invalid("This forum requires at least one tag on every post.");

        if (requested.Count > ForumPostTag.MaxTagsPerPost)
            return SetTagsResult.Invalid($"A post can carry at most {ForumPostTag.MaxTagsPerPost} tags.");

        var forumTags = await ctx.ForumTags
            .Where(t => t.ChannelId == forumChannelId)
            .ToListAsync();

        var byId = forumTags.ToDictionary(t => t.Id);

        // Fail the whole request on an unknown id rather than silently dropping it - a client
        // sending a stale tag id needs to know its picker is out of date.
        var unknown = requested.Where(id => !byId.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            return SetTagsResult.Invalid($"Tag(s) {string.Join(", ", unknown)} do not belong to this forum.");

        var existing = await ctx.ForumPostTags.Where(pt => pt.ThreadChannelId == postId).ToListAsync();
        var existingIds = existing.Select(pt => pt.TagId).ToHashSet();
        var requestedIds = requested.ToHashSet();

        var added = requestedIds.Except(existingIds).ToList();
        var removed = existingIds.Except(requestedIds).ToList();

        var touchedModerated = added.Concat(removed)
            .Where(id => byId.TryGetValue(id, out var tag) && tag.Moderated)
            .ToList();

        if (touchedModerated.Count > 0 && !callerIsModerator)
            return SetTagsResult.Denied("Only moderators can apply or remove a moderated tag.");

        foreach (var pt in existing.Where(pt => removed.Contains(pt.TagId)))
            ctx.ForumPostTags.Remove(pt);

        foreach (var tagId in added)
            ctx.ForumPostTags.Add(new ForumPostTag
            {
                ThreadChannelId = postId,
                TagId = tagId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var ordered = requested
            .OrderBy(id => byId[id].Position)
            .ThenBy(id => id)
            .ToList();

        return SetTagsResult.Ok(ordered);
    }

    /// <summary>The value a post sorts on under <see cref="ForumSortOrder.LatestActivity"/>.
    /// Falls back to creation time so posts with no messages - and every post created before
    /// LastActivityAt shipped - still order sensibly instead of sinking to the bottom.</summary>
    public static DateTimeOffset SortKey(Channel post, ForumSortOrder sort) =>
        sort == ForumSortOrder.LatestActivity ? post.LastActivityAt ?? post.CreatedAt : post.CreatedAt;

    // ── Keyset cursors ───────────────────────────────────────────────────────
    // "{pinned}|{ticks}|{id}", base64url.

    public static string EncodeCursor(bool isPinned, DateTimeOffset sortKey, string id)
    {
        var raw = $"{(isPinned ? 1 : 0)}|{sortKey.UtcTicks}|{id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecodeCursor(string cursor, out bool isPinned, out DateTimeOffset sortKey, out string id)
    {
        isPinned = false;
        sortKey = default;
        id = string.Empty;

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);

            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length != 3) return false;

            isPinned = parts[0] == "1";
            if (!long.TryParse(parts[1], out var ticks)) return false;
            sortKey = new DateTimeOffset(ticks, TimeSpan.Zero);
            id = parts[2];

            return !string.IsNullOrEmpty(id);
        }
        catch (FormatException)
        {
            // A malformed cursor is a client bug, not a server error - the endpoint turns this
            // into a 400 rather than letting a base64 exception escape as a 500.
            return false;
        }
    }
}
