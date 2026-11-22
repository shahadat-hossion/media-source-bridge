using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Jellyfin.Plugin.LinkAdder.Services;

internal static class MediaOperationLimits
{
    private static readonly ConcurrencyLimiter ExpensiveOperations = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 8,
        QueueLimit = 0
    });
    private static readonly PartitionedRateLimiter<Guid> UserOperations = PartitionedRateLimiter.Create<Guid, Guid>(
        id => RateLimitPartition.GetSlidingWindowLimiter(id, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        }));
    private static readonly PartitionedRateLimiter<Guid> UserConcurrency = PartitionedRateLimiter.Create<Guid, Guid>(
        id => RateLimitPartition.GetConcurrencyLimiter(id, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 2,
            QueueLimit = 0
        }));
    internal static Guid UserId(ClaimsPrincipal user) => Guid.TryParse(user.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    internal static RateLimitLease AcquireRate(Guid user) => UserOperations.AttemptAcquire(user);

    internal static IDisposable? TryEnter(Guid user)
    {
        using var rate = AcquireRate(user);
        if (!rate.IsAcquired) return null;
        var userSlot = UserConcurrency.AttemptAcquire(user);
        if (!userSlot.IsAcquired)
        {
            userSlot.Dispose();
            return null;
        }
        var slot = ExpensiveOperations.AttemptAcquire(1);
        if (slot.IsAcquired) return new OperationLease(userSlot, slot);
        slot.Dispose();
        userSlot.Dispose();
        return null;
    }

    private sealed class OperationLease(RateLimitLease userSlot, RateLimitLease slot) : IDisposable
    {
        public void Dispose()
        {
            slot.Dispose();
            userSlot.Dispose();
        }
    }
}
