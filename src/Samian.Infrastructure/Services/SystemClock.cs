using Samian.Domain.Contracts;

namespace Samian.Infrastructure.Services;

// The IClock adapter. `SystemClock` is the entire reason the Domain has a clock PORT: a unit test can
// hand the aggregate a fixed instant and assert that OrderPlaced.OccurredOn is exactly that instant,
// which is impossible against DateTime.UtcNow inside Order.
public sealed class SystemClock : IClock
{
    // UTC, never local time: document timestamps cross time zones in an ERP, and a local DateTimeOffset
    // makes "was this placed before the deadline?" depend on which server answered.
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
