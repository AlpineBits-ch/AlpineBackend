using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Hands back the long-term public key of every device of a set of users, and consumes nothing.
/// </summary>
public class GetUserDeviceKeysHandler
{
    public static async Task<GetUserDeviceKeysResponse> Handle(
        GetUserDeviceKeysRequest request,
        MicroserviceContext ctx)
    {
        var requested = request.UserIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

        if (requested.Count == 0) return new GetUserDeviceKeysResponse();

        // Truncate, but say so.
        var userIds = requested.Take(GetUserDeviceKeysRequest.MaxUserIds).ToList();
        var omitted = requested.Skip(GetUserDeviceKeysRequest.MaxUserIds).ToList();

        var now = DateTimeOffset.UtcNow;

        // No Status filter, unlike GetUserDevicesHandler: a removed device is reported as inactive
        // rather than left out, so a caller can tell the roster shrank instead of quietly sealing
        // to a smaller set.
        var devices = await ctx.UserDevices.AsNoTracking()
            .Where(d => userIds.Contains(d.UserId))
            .Select(d => new
            {
                d.UserId,
                d.ClientDeviceId,
                d.DeviceName,
                // The long-term key the certificate is issued over, not a KeyPackage init key.
                d.IdentityPublicKey,
                d.Status,
                d.Certificate,
                d.CertificateIssuedAt,
                d.CertificateExpiresAt,
                d.CertificateIdentityKeyVersion,
                d.LastSeen,
            })
            .ToListAsync();

        if (devices.Count == 0)
        {
            return new GetUserDeviceKeysResponse { OmittedUserIds = omitted };
        }

        // Revocation is recorded against the certificate's fingerprint, not against the device, so
        // the join has to be on the hash.
        var revocations = await ctx.RevokedDeviceCertificates.AsNoTracking()
            .Where(r => userIds.Contains(r.UserId))
            .Select(r => new { r.UserId, r.CertificateFingerprint, r.RevokedAt })
            .ToListAsync();

        // Earliest wins where a fingerprint was revoked twice: the question a caller asks is "since
        // when should this have been distrusted", and the later row is a duplicate, not an update.
        var revokedAt = revocations
            .GroupBy(r => (r.UserId, r.CertificateFingerprint))
            .ToDictionary(g => g.Key, g => g.Min(r => r.RevokedAt));

        var results = devices
            .Select(d =>
            {
                DateTimeOffset? certificateRevokedAt = null;
                if (d.Certificate is { Length: > 0 } certificate
                    && revokedAt.TryGetValue(
                        (d.UserId, RevokedDeviceCertificate.Fingerprint(certificate)), out var when))
                {
                    certificateRevokedAt = when;
                }

                return new UserDeviceKeyDto
                {
                    UserId = d.UserId,
                    DeviceId = d.ClientDeviceId,
                    DeviceName = d.DeviceName,
                    PublicKey = d.IdentityPublicKey,
                    // Same definition as UserDeviceSummaryResponse.HasValidCertificate - present
                    // and unexpired - so the two contracts cannot disagree about one device.
                    HasValidCertificate = d.Certificate != null && d.CertificateExpiresAt > now,
                    Certificate = d.Certificate,
                    CertificateIssuedAt = d.CertificateIssuedAt,
                    CertificateExpiresAt = d.CertificateExpiresAt,
                    CertificateIdentityKeyVersion = d.CertificateIdentityKeyVersion,
                    CertificateRevokedAt = certificateRevokedAt,
                    IsActive = d.Status == DeviceStatus.Active,
                    LastSeen = d.LastSeen,
                };
            })
            .OrderBy(d => d.UserId, StringComparer.Ordinal)
            .ThenBy(d => d.DeviceId, StringComparer.Ordinal)
            .ToList();

        return new GetUserDeviceKeysResponse { Devices = results, OmittedUserIds = omitted };
    }
}
