using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Commands;

/// <summary>
/// Identity's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling
/// of <see cref="PurgeUserDataCommandHandler"/>.
/// </summary>
public class ExportUserDataCommandHandler
{
    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command,
        MicroserviceContext ctx,
        ILogger<ExportUserDataCommandHandler> logger)
    {
        var request = await ctx.DataExportRequests.FirstOrDefaultAsync(r => r.Id == command.ExportId);
        request?.BeginRunning(DateTimeOffset.UtcNow);

        var user = await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId);

        if (user is null)
        {
            logger.LogWarning("Data export {ExportId} asked Identity for unknown account {UserId}",
                command.ExportId, command.UserId);

            return Empty(command, "No account with that id exists in Identity.");
        }

        var preferences = await ctx.UserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == user.UserPreferencesId);

        var privacy = await ctx.UserPrivacySettings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == command.UserId);

        var devices = await ctx.UserDevices.AsNoTracking()
            .Where(d => d.UserId == command.UserId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();

        var sessions = await ctx.LoginSessions.AsNoTracking()
            .Where(s => s.UserId == command.UserId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync();

        var auditEvents = await ctx.IdentityAuditEvents.AsNoTracking()
            .Where(a => a.UserId == command.UserId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        var consents = await ctx.UserConsents.AsNoTracking()
            .Where(c => c.UserId == command.UserId)
            .OrderBy(c => c.AcceptedAt)
            .ToListAsync();

        var pushTokens = await ctx.UserPushTokens.AsNoTracking()
            .Where(t => t.UserId == command.UserId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        var exportRequests = await ctx.DataExportRequests.AsNoTracking()
            .Where(r => r.UserId == command.UserId)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync();

        var fragment = new
        {
            account = new
            {
                id = user.Id,
                userName = user.UserName,
                email = user.Email,
                emailConfirmed = user.EmailConfirmed,
                phoneNumber = user.PhoneNumber,
                phoneNumberConfirmed = user.PhoneNumberConfirmed,
                bio = user.Bio,
                birthDate = user.AgeVerification?.BirthDate,
                wasVerifiedAdult = user.WasVerifiedAdult,
                status = user.Status.ToString(),
                userType = user.UserType.ToString(),
                steamId = user.SteamId,
                federatedServerId = user.FederatedServerId,
                createdAt = user.CreatedAt,
                onboardedAt = user.OnboardedAt,
                deletionRequestedAt = user.DeletionRequestedAt,
                purgeScheduledAt = user.PurgeScheduledAt,
                interests = user.Interests.ToString(),
                protectionLevel = user.ProtectionLevel.ToString(),
                // Client-owned UI state with no server semantics (T0-6).
                clientSettings = user.JsonSettings,
            },
            ageVerification = user.AgeVerification is null ? null : new
            {
                level = user.AgeVerification.Level.ToString(),
                user.AgeVerification.SelfDeclarationCompletedAt,
                user.AgeVerification.AiEstimationCompletedAt,
                user.AgeVerification.GovermentIdCompletedAt,
            },
            preferences = preferences is null ? null : new
            {
                theme = preferences.Theme.ToString(),
                data = preferences.Data,
                // The legacy privacy flags, still readable off GET /users/self.
                legacyPrivacySettings = preferences.PrivacySettings.ToString(),
                legacyDirectMessageSettings = preferences.DirectMessageSettings.ToString(),
            },
            privacySettings = privacy is null ? null : new
            {
                privacy.AllowDataCollection,
                privacy.AllowPersonalization,
                privacy.AllowVoiceRecordingInClips,
                directMessagePolicy = privacy.DirectMessagePolicy.ToString(),
                friendRequestPolicy = privacy.FriendRequestPolicy.ToString(),
                privacy.DiscoverableByUsername,
                privacy.DiscoverableByEmail,
                privacy.DiscoverableByPhone,
                mutualServersVisibility = privacy.MutualServersVisibility.ToString(),
                mutualFriendsVisibility = privacy.MutualFriendsVisibility.ToString(),
                connectionsVisibility = privacy.ConnectionsVisibility.ToString(),
                birthdayVisibility = privacy.BirthdayVisibility.ToString(),
                privacy.ShareActivity,
                privacy.AllowPositionalVoiceCapture,
                privacy.SendReadReceipts,
                privacy.SendTypingIndicators,
                privacy.DmRetentionDays,
                explicitContentFilter = privacy.ExplicitContentFilter.ToString(),
                privacy.HidePushContent,
                privacy.Version,
            },
            devices = devices.Select(d => new
            {
                d.Id,
                d.ClientDeviceId,
                d.DeviceName,
                deviceType = d.DeviceType.ToString(),
                status = d.Status.ToString(),
                d.LastSeen,
                d.CreatedAt,
                d.Capabilities,
                // No IdentityPublicKey, no Certificate, no key packages, no backup blobs.
            }),
            loginSessions = sessions.Select(s => new
            {
                s.Id,
                s.DeviceName,
                deviceType = s.DeviceType.ToString(),
                s.IpAddress,
                s.UserAgent,
                s.CreatedAt,
                s.LastUsedAt,
                s.RevokedAt,
                s.DeviceId,
            }),
            securityAuditLog = auditEvents.Select(a => new
            {
                a.Id,
                a.Action,
                a.Detail,
                a.IpAddress,
                a.ClientDeviceId,
                a.CreatedAt,
            }),
            legalConsents = consents.Select(c => new
            {
                c.Id,
                documentType = c.DocumentType.ToString(),
                c.Version,
                c.AcceptedAt,
                c.IpAddress,
            }),
            pushRegistrations = pushTokens.Select(t => new
            {
                t.Id,
                kind = t.Kind.ToString(),
                t.DeviceId,
                t.CreatedAt,
                // The token itself is a credential for pushing to that handset, not information
                // about the subject. Deliberately absent.
            }),
            dataExportRequests = exportRequests.Select(r => new
            {
                r.Id,
                status = r.Status.ToString(),
                r.RequestedAt,
                r.CompletedAt,
                r.ExpiresAt,
                // Never the ArtifactKey - it is a bearer credential for an archive.
            }),
        };

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "identity",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>
            {
                ["account"] = 1,
                ["preferences"] = preferences is null ? 0 : 1,
                ["privacySettings"] = privacy is null ? 0 : 1,
                ["devices"] = devices.Count,
                ["loginSessions"] = sessions.Count,
                ["securityAuditLog"] = auditEvents.Count,
                ["legalConsents"] = consents.Count,
                ["pushRegistrations"] = pushTokens.Count,
                ["dataExportRequests"] = exportRequests.Count,
            },
        };
    }

    private static ExportUserDataResponse Empty(ExportUserDataCommand command, string error) => new()
    {
        ExportId = command.ExportId,
        UserId = command.UserId,
        Service = "identity",
        FragmentJson = "{}",
        Error = error,
    };
}
