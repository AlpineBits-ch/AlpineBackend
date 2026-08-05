using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>The read half of the moderation console's user surface.</summary>
public class AdminUserQueryHandler
{
    /// <summary>Hard ceiling on a page, whatever the caller asks for.</summary>
    public const int MaxLimit = 100;

    public async Task<SearchUsersResponse> Handle(SearchUsersRequest request, MicroserviceContext ctx)
    {
        var query = ctx.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Status) &&
            Enum.TryParse<UserStatus>(request.Status, true, out var status))
        {
            query = query.Where(u => u.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(request.UserType) &&
            Enum.TryParse<UserType>(request.UserType, true, out var type))
        {
            query = query.Where(u => u.UserType == type);
        }

        var term = request.Query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // Matched against the normalised columns, which are what Identity indexes.
            var normalised = term.ToUpperInvariant();

            query = query.Where(u =>
                u.Id == term
                || (u.NormalizedUserName != null && u.NormalizedUserName.Contains(normalised))
                || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalised)));
        }

        // Counted before paging, so the console can say "1-25 of 812" truthfully.
        var total = await query.CountAsync();

        var limit = Math.Clamp(request.Limit <= 0 ? 25 : request.Limit, 1, MaxLimit);
        var offset = Math.Max(0, request.Offset);

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Skip(offset)
            .Take(limit)
            .Select(u => new AdminUserSummary
            {
                Id = u.Id,
                UserName = u.UserName,
                Email = u.Email,
                Status = u.Status.ToString(),
                UserType = u.UserType.ToString(),
                EmailVerified = u.EmailVerifiedAt != null,
                CreatedAt = u.CreatedAt,
            })
            .ToListAsync();

        return new SearchUsersResponse { Users = users, Total = total };
    }

    public async Task<GetAdminUserDetailResponse> Handle(GetAdminUserDetailRequest request, MicroserviceContext ctx)
    {
        var now = DateTimeOffset.UtcNow;

        var user = await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId);

        if (user is null) return new GetAdminUserDetailResponse();

        var deviceCount = await ctx.UserDevices.CountAsync(d => d.UserId == user.Id);

        // The most recent unrevoked session's last use.
        var lastSignIn = await ctx.LoginSessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .OrderByDescending(s => s.LastUsedAt)
            .Select(s => (DateTimeOffset?)s.LastUsedAt)
            .FirstOrDefaultAsync();

        return new GetAdminUserDetailResponse
        {
            User = new AdminUserSummary
            {
                Id = user.Id,
                UserName = user.UserName,
                Email = user.Email,
                Status = user.Status.ToString(),
                UserType = user.UserType.ToString(),
                EmailVerified = user.EmailVerifiedAt != null,
                CreatedAt = user.CreatedAt,
            },
            Bio = user.Bio,
            EmailVerifiedAt = user.EmailVerifiedAt,
            TwoFactorEnabled = user.TwoFactorEnabled,
            LockedOut = user.LockoutEnd != null && user.LockoutEnd > now,
            LockoutEnd = user.LockoutEnd,
            DeviceCount = deviceCount,
            DeletionRequestedAt = user.DeletionRequestedAt,
            LastSignInAt = lastSignIn,
        };
    }

    public async Task<GetPlatformStatsResponse> Handle(GetPlatformStatsRequest _, MicroserviceContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        var users = ctx.Users.AsNoTracking();

        // Eleven counts, deliberately as eleven queries rather than one grouped scan.
        return new GetPlatformStatsResponse
        {
            TotalUsers = await users.CountAsync(),
            ActiveUsers = await users.CountAsync(u => u.Status == UserStatus.Active),
            BannedUsers = await users.CountAsync(u => u.Status == UserStatus.Banned),
            PendingDeletion = await users.CountAsync(u =>
                u.Status == UserStatus.PendingDeletion || u.Status == UserStatus.PurgeInProgress),
            DeletedUsers = await users.CountAsync(u => u.Status == UserStatus.Deleted),
            BotAccounts = await users.CountAsync(u => u.UserType == UserType.Bot),
            StaffAccounts = await users.CountAsync(u =>
                u.UserType == UserType.Admin || u.UserType == UserType.Moderator),

            // Bots have no address to verify and tombstones have had theirs erased; counting either
            // as "unverified" would make the number a permanent, unactionable non-zero.
            UnverifiedEmail = await users.CountAsync(u =>
                u.EmailVerifiedAt == null && u.UserType != UserType.Bot && u.Status != UserStatus.Deleted),

            NewUsers24h = await users.CountAsync(u => u.CreatedAt >= now.AddDays(-1)),
            NewUsers7d = await users.CountAsync(u => u.CreatedAt >= now.AddDays(-7)),
            NewUsers30d = await users.CountAsync(u => u.CreatedAt >= now.AddDays(-30)),
        };
    }
}
