using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Dtos.Response;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>Which devices of a set of users were left outside an MLS group.</summary>
public class MlsDeviceCoverageService(IMessageBus bus, ILogger<MlsDeviceCoverageService> logger)
{
    /// <summary>
    /// The active devices of <paramref name="userIds"/> that are not in <paramref name="covered"/>.
    /// </summary>
    /// <param name="contextLabel">
    /// Context id, or a description of what is being created - used only for the log line.
    /// </param>
    /// <param name="userIds">Whose devices to account for.</param>
    /// <param name="covered">
    /// Every <c>(userId, clientDeviceId)</c> pair that already holds, or is being handed, a leaf.
    /// </param>
    public async Task<List<UnreachableDeviceDto>> ResolveAsync(
        string contextLabel,
        IReadOnlyCollection<string> userIds,
        IReadOnlySet<(string UserId, string DeviceId)> covered)
    {
        var targets = userIds
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct()
            .ToList();

        if (targets.Count == 0) return [];

        GetUserDevicesResponse devices;
        try
        {
            devices = await bus.InvokeAsync<GetUserDevicesResponse>(
                new GetUserDevicesRequest { UserIds = targets });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not resolve device coverage for MLS context {Context}: Identity did not answer. "
                + "Devices left out of this group will not be reported to the caller.",
                contextLabel);
            return [];
        }

        var missing = devices.Devices
            .Where(d => !covered.Contains((d.UserId, d.ClientDeviceId)))
            .Select(d => new UnreachableDeviceDto
            {
                UserId = d.UserId,
                DeviceId = d.ClientDeviceId,
                DeviceName = d.DeviceName,
            })
            .ToList();

        if (missing.Count > 0)
        {
            // Warning, not information.
            logger.LogWarning(
                "MLS context {Context} was formed without {Count} active device(s): {Devices}. "
                + "Those devices hold no leaf and cannot read it until a member admits them.",
                contextLabel,
                missing.Count,
                string.Join(", ", missing.Select(d => $"{d.UserId}/{d.DeviceId}")));
        }

        return missing;
    }
}
