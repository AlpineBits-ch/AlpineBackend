using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Dtos.Response;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>
/// Which devices of a set of users were left outside an MLS group.
///
/// <para><b>An MLS group member is a device, not a person.</b> Every check that asks whether a
/// <i>user</i> made it into the group passes as soon as any one of their handsets did, and the rest
/// are dropped in silence - which reads to the person holding the dropped one as the conversation
/// simply being broken. The symptom is unmistakable and its cause is invisible from both ends: the
/// same account reads the thread on the phone and sees nothing on the desktop, and nobody, at any
/// point, was told a device was skipped.</para>
///
/// <para>The server cannot fix that - it holds no group keys, so only a member's client can mint an
/// Add commit - but it is the only party that can see the whole picture, because it is the only one
/// that knows every device of every participant. So the one thing it must do is <b>say so</b>: to
/// the caller in the response, and to the operator in the log. A skip that nobody is told about is
/// how this becomes a user-visible mystery instead of an error someone can act on.</para>
///
/// <para>Failure to reach Identity yields an empty list rather than an exception: a degraded
/// coverage report is a much better outcome than being unable to create a conversation or publish a
/// commit because a sibling service is down. The degradation is logged, so an operator sees the
/// difference between "everyone is covered" and "we could not tell".</para>
///
/// <para><b>An empty list is not proof of full coverage.</b> This can only account for devices that
/// have a <c>user_devices</c> row, so a device that never completed registration is invisible here
/// and to the caller alike. That is not an oversight to be patched at this layer: nothing in the
/// schema retains a client device id that has no row - sessions and push tokens resolve it and store
/// null on a miss, backups and transfers refuse the write outright - so there is no signal that such
/// a device exists, and inventing one from "this account has a session with no device" would fire on
/// most accounts forever, for reasons unrelated to MLS. See contract §L.14.1. The remedy for an
/// unregistered device is the join-request path it can use once it registers, not a guess made
/// here.</para>
/// </summary>
public class MlsDeviceCoverageService(IMessageBus bus, ILogger<MlsDeviceCoverageService> logger)
{
    /// <summary>
    /// The active devices of <paramref name="userIds"/> that are not in <paramref name="covered"/>.
    /// </summary>
    /// <param name="contextLabel">Context id, or a description of what is being created - used only
    /// for the log line.</param>
    /// <param name="userIds">Whose devices to account for. Callers must include <b>themselves</b>
    /// when their own other devices belong in the group, which is every case except a context they
    /// are not a participant in.</param>
    /// <param name="covered">Every <c>(userId, clientDeviceId)</c> pair that already holds, or is
    /// being handed, a leaf. That includes the calling device itself: it built the group and so has
    /// no Welcome addressed to it, and reporting it would be a false positive on the one device that
    /// certainly can read the conversation.</param>
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
            // Warning, not information. Each entry is a device that will never read a word of this
            // context unless somebody later admits it, and the person holding it gets no error of
            // their own - the message simply does not decrypt.
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
