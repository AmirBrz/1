//Keeps entity methods readable instead of drowning in ifs, and centralizes which exception each violation throws. 10 lines, saves 15 min of typing.
using Samian.Domain.Exceptions;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Common;

// static (was instantiable): a guard is a rule, not a thing you ever need "new Guard()" of.
public static class Guard
{
    // Why Func<Exception> instead of Exception: an exception captures a stack trace when it is CREATED.
    // Passing `new InsufficientStockException()` built that object on every single call, even when the
    // check passed. With a factory it is only constructed when the rule actually fails.
    public static void AgainstNegative(decimal value, Func<Exception> exceptionFactory)
    {
        if (value < 0) throw exceptionFactory();
    }

    public static void AgainstNegative(int value, Func<Exception> exceptionFactory)
    {
        if (value < 0) throw exceptionFactory();
    }

    // A business count (quantity, stock) is never zero-or-less; negative-and-zero needed two calls before.
    public static void AgainstNonPositive(int value, Func<Exception> exceptionFactory)
    {
        if (value <= 0) throw exceptionFactory();
    }

    public static void AgainstEmpty(string? value, Func<Exception> exceptionFactory)
    {
        if (string.IsNullOrWhiteSpace(value)) throw exceptionFactory();
    }

    public static void AgainstNull(object? value, Func<Exception> exceptionFactory)
    {
        if (value is null) throw exceptionFactory();
    }

    // Reads as a sentence at the call site: Guard.AgainstInvalidState(canCancel, () => new ...)
    public static void AgainstInvalidState(bool condition, Func<Exception> exceptionFactory)
    {
        if (!condition) throw exceptionFactory();
    }

    public static void AgainstZero(decimal value, Func<Exception> exceptionFactory)
    {
        if (value == 0) throw exceptionFactory();
    }

    // Mixed-currency arithmetic is a BUSINESS rule, so it throws a DomainException (not ArgumentException):
    // the middleware maps one base type and returns 400 instead of a 500 for every rule violation.
    public static void AgainstMixedCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new DomainException($"Cannot combine {left.Currency} with {right.Currency}.");
    }
}
