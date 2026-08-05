using AppEnvironment;
using Identity.Application.Services;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Resolves the privacy record of a set of accounts for the services that enforce it.
/// </summary>
public class GetUserPrivacySettingsHandler
{
    public static async Task<GetUserPrivacySettingsResponse> Handle(
        GetUserPrivacySettingsRequest request,
        MicroserviceContext ctx)
    {
        var userIds = request.UserIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? [];
        if (userIds.Count == 0) return new GetUserPrivacySettingsResponse();

        var stored = await ctx.UserPrivacySettings.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .Select(p => new UserPrivacySettingsSummary
            {
                UserId = p.UserId,
                AllowDataCollection = p.AllowDataCollection,
                AllowPersonalization = p.AllowPersonalization,
                AllowVoiceRecordingInClips = p.AllowVoiceRecordingInClips,
                DirectMessagePolicy = p.DirectMessagePolicy,
                FriendRequestPolicy = p.FriendRequestPolicy,
                DiscoverableByUsername = p.DiscoverableByUsername,
                DiscoverableByEmail = p.DiscoverableByEmail,
                DiscoverableByPhone = p.DiscoverableByPhone,
                MutualServersVisibility = p.MutualServersVisibility,
                MutualFriendsVisibility = p.MutualFriendsVisibility,
                ConnectionsVisibility = p.ConnectionsVisibility,
                BirthdayVisibility = p.BirthdayVisibility,
                ShareActivity = p.ShareActivity,
                AllowPositionalVoiceCapture = p.AllowPositionalVoiceCapture,
                SendReadReceipts = p.SendReadReceipts,
                SendTypingIndicators = p.SendTypingIndicators,
                DmRetentionDays = p.DmRetentionDays,
                ExplicitContentFilter = p.ExplicitContentFilter,
                HidePushContent = p.HidePushContent,
                Version = p.Version,
            })
            .ToListAsync();

        var answered = stored.Select(s => s.UserId).ToHashSet();
        foreach (var missing in userIds.Where(id => !answered.Contains(id)))
        {
            stored.Add(PrivacySettingsMapping.ToSummary(
                UserPrivacySettings.CreateDefault(missing, DateTimeOffset.UtcNow)));
        }

        // T1-11. The minor floors are applied here as well as at the REST surface, because this is
        // where every other service actually resolves policy from.
        var now = DateTimeOffset.UtcNow;
        var ageOfMajority = Env.Privacy.AgeOfMajority;

        var birthDates = await ctx.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.AgeVerification.BirthDate })
            .ToListAsync();

        var minors = birthDates
            .Where(u => new AgeVerification { BirthDate = u.BirthDate }.IsMinorAt(now, ageOfMajority))
            .Select(u => u.Id)
            .ToHashSet();

        foreach (var summary in stored)
        {
            MinorPrivacyFloors.Clamp(summary, minors.Contains(summary.UserId));
        }

        // Loaded after the floors, not before: a suppression is the user asking for less to be
        // shared, so no floor can widen it and clamping it would be meaningless.
        var hidden = await ctx.UserHiddenActivities.AsNoTracking()
            .Where(h => userIds.Contains(h.UserId))
            .Select(h => new { h.UserId, h.ApplicationId, h.Name })
            .ToListAsync();

        if (hidden.Count > 0)
        {
            var byUser = hidden.GroupBy(h => h.UserId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var summary in stored)
            {
                if (!byUser.TryGetValue(summary.UserId, out var rows)) continue;

                summary.HiddenActivities = new HiddenActivitySummary
                {
                    ApplicationIds = rows.Where(r => r.ApplicationId is not null)
                        .Select(r => r.ApplicationId!).ToList(),
                    Names = rows.Where(r => r.Name is not null).Select(r => r.Name!).ToList(),
                };
            }
        }

        return new GetUserPrivacySettingsResponse { Settings = stored };
    }
}
