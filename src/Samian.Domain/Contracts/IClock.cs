namespace Samian.Domain.Contracts;

// A PORT for time. Business-sensitive, so it lives in the Domain (unlike IJwtTokenGenerator, which
// is an auth concern and belongs to Application).
//
// Two changes from `DateTime Now()`, both defensible in an interview:
// - DateTimeOffset instead of DateTime: a bare DateTime carries no zone, so "Now" silently compares
//   wall-clock values across UTC/local and drifts the moment data crosses a server boundary.
// - a property, not a method: reading the clock is a value lookup, and `UtcNow` reads naturally in
//   the application service that injects it (var now = _clock.UtcNow).
//
// Why the port exists at all: an entity that reads the system clock cannot be tested at a fixed
// instant, so the service reads the clock and PASSES the value into the aggregate
// (Order.PlaceOrder(now)) — the domain stays deterministic.
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
