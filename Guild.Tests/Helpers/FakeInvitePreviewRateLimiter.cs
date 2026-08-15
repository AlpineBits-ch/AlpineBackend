using Guild.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Helpers;

/// <summary>
/// An <see cref="InvitePreviewRateLimiter"/> whose answer is set by the test rather than by Redis.
/// </summary>
public sealed class FakeInvitePreviewRateLimiter(bool allow = true)
    : InvitePreviewRateLimiter(RedisTestFactory.Create(), NullLogger<InvitePreviewRateLimiter>.Instance)
{
    public int Calls { get; private set; }

    public override Task<bool> TryTakeAsync(string partition, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(allow);
    }
}
