// Money — amount + currency, immutable, arithmetic operators, rejects negative, throws on mixed-currency addition, static Zero(currency).
// Why: decimal alone loses the currency dimension and every calculation downstream becomes unsafe. Consider readonly struct — and be ready to say why (no allocation, no null, no identity to track).
// Why struct : no null, no identity to track, no allocation, no virtual methods, no inheritance, no casting, no boxing, no unboxing, no garbage, no bounds checking, no null checking, no validation, no logging, no debugging, no testing, no maintenance, no support, no documentation, no nothing.
// A value object compares by all its properties, is immutable, and has no identity.
using System.Text.RegularExpressions;
using Samian.Domain.Common;
using Samian.Domain.Exceptions;

namespace Samian.Domain.ValueObjects;

// Deliberately a readonly struct while DocumentNumber is a class: Money sits in loops (line totals,
// order totals), so allocation-free + can't-be-null is worth more than sharing the ValueObject base.
// Because a struct gets its own copy semantics, it needs IEquatable + == so it never falls back to
// slow reflection-based ValueType.Equals, and so `money1 == money2` compares values, not boxes.
public readonly struct Money : IEquatable<Money>
{
    private static readonly Regex CurrencyPattern =
        new(@"^[A-Z]{3}$", RegexOptions.Compiled); // IRR, USD, EUR — three uppercase letters

    public readonly decimal Amount;
    public readonly string Currency;

    public Money(decimal amount, string currency)
    {
        Guard.AgainstNegative(amount, () => new DomainException("Amount cannot be negative."));
        Guard.AgainstEmpty(currency, () => new DomainException("Currency is required."));
        Guard.AgainstInvalidState(CurrencyPattern.IsMatch(currency),
            () => new DomainException($"'{currency}' is not a valid currency code (expected 3 uppercase letters)."));

        Amount = amount;
        Currency = currency;
    }

    public static Money operator +(Money left, Money right)
    {
        Guard.AgainstMixedCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        Guard.AgainstMixedCurrency(left, right);
        Guard.AgainstNegative(left.Amount - right.Amount, () => new DomainException("Result cannot be negative."));
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    // `int quantity * Money` and `Money / int` are the shapes the order math actually needs;
    // the int overloads stop the compiler from quietly boxing decimals through decimal->int widening.
    public static Money operator *(Money left, decimal right)
    {
        Guard.AgainstNegative(left.Amount * right, () => new DomainException("Result cannot be negative."));
        return new Money(left.Amount * right, left.Currency);
    }

    public static Money operator *(Money left, int right) => left * (decimal)right;

    public static Money operator /(Money left, decimal right)
    {
        Guard.AgainstZero(right, () => new DomainException("Divisor cannot be zero."));
        Guard.AgainstNegative(left.Amount / right, () => new DomainException("Result cannot be negative."));
        return new Money(left.Amount / right, left.Currency);
    }

    public static Money operator /(Money left, int right) => left / (decimal)right;

    // COMPARISONS. A discount rule is "is this amount at least that amount?", and doing that with
    // `.Amount` alone silently compares a USD figure against an IRR one. Comparing is arithmetic as
    // far as money is concerned, so it goes through the same currency guard.
    public static bool operator <(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount < right.Amount;
    }

    public static bool operator >(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount > right.Amount;
    }

    public static bool operator <=(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount <= right.Amount;
    }

    public static bool operator >=(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount >= right.Amount;
    }

    // Explicit helper (rather than reusing AgainstMixedCurrency) because the guard clause in that
    // method is about combining values; here the intent reads as a precondition of a comparison.
    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new DomainException($"Cannot compare {left.Currency} with {right.Currency}.");
    }

    public static Money Zero(string currency) => new(0, currency);

    // IEquatable<Money> requires the NON-nullable parameter; a struct can never be null, so `Money?`
    // here would both break the interface and add a pointless Nullable wrapper on every comparison.
    public bool Equals(Money other) => Amount == other.Amount && Currency == other.Currency;

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public override string ToString() => $"{Amount:0.##} {Currency}";
}
