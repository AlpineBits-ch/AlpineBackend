using System.Security.Claims;
using Identity.Application.Services;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;

namespace Identity.Tests.Helpers;

/// <summary>Seeds the <c>LoginSession</c> rows that bind a caller to a device, which is what
/// replaced the self-asserted <c>X-Device-Id</c> header on every per-device route.</summary>
internal static class TestSessions
{
    /// <summary>A session for <paramref name="userId"/> bound to <paramref name="deviceRowId"/>, and
    /// a principal carrying its id.</summary>
    public static async Task<ClaimsPrincipal> SignedInOnAsync(
        MicroserviceContext ctx, string userId, string? deviceRowId, DeviceType type = DeviceType.Desktop)
    {
        var session = LoginSession.Create(new CreateLoginSessionParams
        {
            UserId = userId,
            DeviceName = "Test session",
            DeviceType = type,
            DeviceId = deviceRowId,
        });

        ctx.LoginSessions.Add(session);
        await ctx.SaveChangesAsync();

        return TestPrincipal.ForSession(userId, session.Id);
    }

    public static SessionDeviceResolver Resolver(MicroserviceContext ctx) => new(ctx);
}
