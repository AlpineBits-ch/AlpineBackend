using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>A member's own notification preferences.</summary>
[Authorize]
public class NotificationSettingEndpoint
{
    /// <summary>Stand-in for "muted until I say otherwise".</summary>
    private static readonly DateTimeOffset Forever = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    private static DateTimeOffset? ResolveMute(int? muteMinutes, bool muteForever, DateTimeOffset? current)
    {
        if (muteForever) return Forever;
        if (muteMinutes is null) return current;
        return muteMinutes <= 0 ? null : DateTimeOffset.UtcNow.AddMinutes(muteMinutes.Value);
    }

    private static async Task<GuildMember?> FindMemberAsync(MicroserviceContext ctx, string guildId, string userId) =>
        await ctx.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);

    // ── Guild level ──────────────────────────────────────────────────────────

    [WolverineGet("/api/v1/guilds/{guildId}/notification-settings")]
    public async Task<IResult> GetAsync(string guildId, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await FindMemberAsync(ctx, guildId, userId);
        if (member is null) return Results.NotFound();

        var setting = await ctx.GuildNotificationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.MemberId == member.Id);

        var overrides = await ctx.NotificationOverrides.AsNoTracking()
            .Where(o => o.MemberId == member.Id)
            .ToListAsync();

        return Results.Ok(GuildNotificationSettingDto.From(guildId, setting, overrides));
    }

    [WolverinePut("/api/v1/guilds/{guildId}/notification-settings")]
    public async Task<IResult> UpdateAsync(string guildId, UpdateGuildNotificationSettingDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var member = await FindMemberAsync(ctx, guildId, userId);
        if (member is null) return Results.NotFound();

        var setting = await ctx.GuildNotificationSettings.FirstOrDefaultAsync(s => s.MemberId == member.Id);
        if (setting is null)
        {
            setting = GuildNotificationSetting.CreateDefault(member.Id);
            ctx.GuildNotificationSettings.Add(setting);
        }

        if (dto.Level is not null) setting.Level = dto.Level.Value;
        setting.MutedUntil = ResolveMute(dto.MuteMinutes, dto.MuteForever, setting.MutedUntil);
        if (dto.SuppressEveryone is not null) setting.SuppressEveryone = dto.SuppressEveryone.Value;
        if (dto.SuppressRoleMentions is not null) setting.SuppressRoleMentions = dto.SuppressRoleMentions.Value;
        if (dto.MobilePush is not null) setting.MobilePush = dto.MobilePush.Value;
        setting.UpdatedAt = DateTimeOffset.UtcNow;

        var overrides = await ctx.NotificationOverrides.AsNoTracking()
            .Where(o => o.MemberId == member.Id)
            .ToListAsync();

        return Results.Ok(GuildNotificationSettingDto.From(guildId, setting, overrides));
    }

    // ── Channel / category overrides ─────────────────────────────────────────

    [WolverinePut("/api/v1/channels/{channelId}/notification-settings")]
    public async Task<IResult> UpsertChannelOverrideAsync(string channelId, UpdateNotificationOverrideDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guildId = await ctx.Channels.AsNoTracking()
            .Where(c => c.Id == channelId).Select(c => c.GuildId).FirstOrDefaultAsync();
        if (guildId is null) return Results.NotFound();

        var member = await FindMemberAsync(ctx, guildId, userId);
        if (member is null) return Results.NotFound();

        var existing = await ctx.NotificationOverrides
            .FirstOrDefaultAsync(o => o.MemberId == member.Id && o.ChannelId == channelId);

        return await UpsertOverrideAsync(ctx, existing, () => NotificationOverride.ForChannel(member.Id, channelId), dto);
    }

    [WolverineDelete("/api/v1/channels/{channelId}/notification-settings")]
    public async Task<IResult> DeleteChannelOverrideAsync(string channelId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var existing = await ctx.NotificationOverrides
            .Include(o => o.GuildMember)
            .FirstOrDefaultAsync(o => o.ChannelId == channelId && o.GuildMember.UserId == userId);

        if (existing is not null) ctx.NotificationOverrides.Remove(existing);

        // Idempotent: removing an override that was never there leaves the member on the
        // inherited setting, which is exactly the requested end state.
        return Results.NoContent();
    }

    [WolverinePut("/api/v1/categories/{categoryId}/notification-settings")]
    public async Task<IResult> UpsertCategoryOverrideAsync(string categoryId, UpdateNotificationOverrideDto dto,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var guildId = await ctx.Categories.AsNoTracking()
            .Where(c => c.Id == categoryId).Select(c => c.GuildId).FirstOrDefaultAsync();
        if (guildId is null) return Results.NotFound();

        var member = await FindMemberAsync(ctx, guildId, userId);
        if (member is null) return Results.NotFound();

        var existing = await ctx.NotificationOverrides
            .FirstOrDefaultAsync(o => o.MemberId == member.Id && o.CategoryId == categoryId);

        return await UpsertOverrideAsync(ctx, existing, () => NotificationOverride.ForCategory(member.Id, categoryId), dto);
    }

    [WolverineDelete("/api/v1/categories/{categoryId}/notification-settings")]
    public async Task<IResult> DeleteCategoryOverrideAsync(string categoryId,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var existing = await ctx.NotificationOverrides
            .Include(o => o.GuildMember)
            .FirstOrDefaultAsync(o => o.CategoryId == categoryId && o.GuildMember.UserId == userId);

        if (existing is not null) ctx.NotificationOverrides.Remove(existing);

        return Results.NoContent();
    }

    private static Task<IResult> UpsertOverrideAsync(MicroserviceContext ctx, NotificationOverride? existing,
        Func<NotificationOverride> create, UpdateNotificationOverrideDto dto)
    {
        var target = existing ?? create();

        target.Level = dto.Level;
        target.MutedUntil = ResolveMute(dto.MuteMinutes, dto.MuteForever, target.MutedUntil);
        target.UpdatedAt = DateTimeOffset.UtcNow;

        // An override expressing neither a level nor a mute is indistinguishable from having none,
        // so it is deleted rather than stored - otherwise every channel a member ever glanced at
        // in the settings UI would accumulate a meaningless row.
        if (target.IsEmpty())
        {
            if (existing is not null) ctx.NotificationOverrides.Remove(existing);
            return Task.FromResult(Results.NoContent());
        }

        if (existing is null) ctx.NotificationOverrides.Add(target);

        return Task.FromResult(Results.Ok(NotificationOverrideDto.From(target)));
    }

    // ── Bulk hydration ───────────────────────────────────────────────────────

    /// <summary>Every notification setting the caller has, across every guild, in one call. The
    /// client needs all of it up front to render unread badges correctly on first paint, and
    /// asking per guild would be one request per guild on every cold start.</summary>
    [WolverineGet("/api/v1/users/me/notification-settings")]
    public async Task<IResult> GetAllForUserAsync([NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var members = await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Id, m.GuildId })
            .ToListAsync();

        if (members.Count == 0) return Results.Ok(new List<GuildNotificationSettingDto>());

        var memberIds = members.Select(m => m.Id).ToList();

        var settings = await ctx.GuildNotificationSettings.AsNoTracking()
            .Where(s => memberIds.Contains(s.MemberId))
            .ToDictionaryAsync(s => s.MemberId);

        var overrides = await ctx.NotificationOverrides.AsNoTracking()
            .Where(o => memberIds.Contains(o.MemberId))
            .ToListAsync();

        var overridesByMember = overrides.GroupBy(o => o.MemberId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = members.Select(m => GuildNotificationSettingDto.From(
            m.GuildId,
            settings.GetValueOrDefault(m.Id),
            overridesByMember.GetValueOrDefault(m.Id, []))).ToList();

        return Results.Ok(result);
    }
}
