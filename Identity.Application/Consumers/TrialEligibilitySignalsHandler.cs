using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// The account facts Billing needs to decide a trial, answered by the service that owns the rows.
/// </summary>
public class TrialEligibilitySignalsHandler
{
    public static async Task<GetTrialEligibilitySignalsResponse> Handle(
        GetTrialEligibilitySignalsRequest request,
        MicroserviceContext ctx)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.UserId)) return new GetTrialEligibilitySignalsResponse();

        var userId = request.UserId.Trim();

        var user = await ctx.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.EmailVerifiedAt,
                u.PhoneNumber,
                u.CreatedAt,
                u.Status,
                u.UserType,
            })
            .FirstOrDefaultAsync();

        if (user is null) return new GetTrialEligibilitySignalsResponse();

        // Active devices only, matching GetUserDevicesHandler.
        var deviceIds = await ctx.UserDevices.AsNoTracking()
            .Where(d => d.UserId == userId && d.Status == DeviceStatus.Active)
            .OrderBy(d => d.ClientDeviceId)
            .Select(d => d.ClientDeviceId)
            .Take(GetTrialEligibilitySignalsResponse.MaxDeviceIds)
            .ToListAsync();

        return new GetTrialEligibilitySignalsResponse
        {
            Found = true,
            EmailVerified = user.EmailVerifiedAt is not null,
            PhoneNumber = string.IsNullOrWhiteSpace(user.PhoneNumber) ? null : user.PhoneNumber,
            CreatedAt = user.CreatedAt,
            DeviceIds = deviceIds,
            Status = user.Status.ToString(),
            IsBot = user.UserType == UserType.Bot,
        };
    }
}
